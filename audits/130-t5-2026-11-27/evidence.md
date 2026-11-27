# 130/T5 evidence — AOT/trim single-binary publish (absorbs 024)

Run date: 2026-11-27. Worktree: `.claude/worktrees/130-sqzrlite`, branch `feat/130-sqzrlite`, head `7227391`.
Toolchain: dotnet SDK 10.0.301, linux-x64, 32 cores, 62 GB RAM.
The verification subject is the PUBLISHED binary, not the build output:
`artifacts/publish-sqzrlite-linux-x64/sqzrlite`, produced by `scripts/publish-sqzrlite.sh` (the pinned shape).

## Publish-shape decision (measured, not guessed)

| attempt | result | exact evidence |
|---------|--------|----------------|
| `PublishAot=true` | **REJECTED — binary dead on arrival** | builds (43,396,792 bytes native) but aborts on EVERY invocation incl. `--version`: `Spectre.Console.Cli.CommandRuntimeException: Could not get settings type for command of type 'Squeezr.Lite.Commands.InitCommand'`, core dump, exit 134. Warned at build time by `IL3050` on the `CommandApp` ctor ("Spectre.Console.Cli relies on reflection. Use during trimming and AOT compilation is not supported"). The ILC log additionally proves the semantic layer statically dead: `Method 'MSBuildWorkspace.OpenProjectAsync/OpenSolutionAsync' will always throw because: Failed to load assembly 'Microsoft.Build.Framework'` (plus the whole `SQLitePCLRaw.core` surface) — the Microsoft.Build packages are `ExcludeAssets="runtime"` and are resolved at RUN TIME by `MSBuildLocator`, which NativeAOT cannot perform. Log: `/var/tmp` probe, key lines quoted in `scripts/publish-sqzrlite.sh` header. |
| `PublishTrimmed=true` (+single-file) | **REJECTED — same death** | same `CommandRuntimeException`, exit 134, on every invocation. Warned by `IL2104`: "Assembly 'Spectre.Console.Cli' produced trim warnings" — the trimmer removes the command settings types Spectre resolves by reflection. LiteDB and the reflection-based `System.Text.Json` writers in Lite sit on the same graph, so a TrimmerRoots patch list would be a wide, fragile effort for zero benefit (see size note below). |
| **SHIPPED: self-contained single-file + `PublishReadyToRun=true`, NO trim, NO AOT, compression OFF** | **works, full e2e green** | 218,339,864 bytes; `dotnet build` 0W/0E added, only pre-existing Core warnings. |

Compression A/B (same shape, only `EnableCompressionInSingleFile` flipped, box load 2–7 on 32 cores):

| variant | binary size | map cold (8-file fixture) | serve → first tool answer |
|---------|-------------|---------------------------|---------------------------|
| compressed | 95.6 MB | 0.55–0.57 s (307 MB peak) | 0.651–0.653 s |
| **uncompressed (shipped)** | 218.3 MB | **0.17–0.18 s (99 MB peak)** | **0.263–0.267 s** |

Decision: startup wins. sqzrlite is invoked per call by agents and serves long-lived MCP sessions, so the
bundle-decompress tax would sit on every invocation; the uncompressed single file is still ONE file with zero
setup, and `--compress` on `scripts/publish-sqzrlite.sh` keeps the small build one flag away (trade recorded in
the script header). Research baseline for the FULL product: ~0.55 s serve-to-first-answer, ~0.37 s map cold —
the shipped lite binary lands 2–2.4× below on both.

## Asset counts

- Published dir `artifacts/publish-sqzrlite-linux-x64/`: **31 entries** — 1 single-file binary + 28 tree-sitter
  native `.so` + `BuildHost-netcore/` (3 files) + `BuildHost-net472/` (2 files). Zero loose managed DLLs — the
  whole managed graph is inside the single file (R2R images included).
- Flat self-contained publish (dependency enumeration, same graph, no bundling): **216 managed DLLs**,
  185,274,790 bytes total. `grep -ci qdrant` → **0**, `grep -ci ollama` → **0**. Build-side bin output:
  **44 DLLs** (matches T4's count). Release tar `sqzrlite-linux-x64.tar.gz` (from `--pack`): 37 entries.

## win-x64 (best effort per spec)

Cross-published from Linux: `sqzrlite.exe`, 214,676,918 bytes, `file` → "PE32+ executable for MS Windows 6.00
(console), x86-64, 9 sections" + 28 win tree-sitter DLLs (R2R crossgen2 to win-x64 included). NOT executed —
no Windows on this box, and CI already parks win-x64 ("until a windows runner exists", `ci.yml` publish-smoke
comment), so this task adopts the same posture: artifact produced, runtime verdict deferred to a windows runner.

## Full end-to-end on the published binary (`run.log`, `run-stdout.log`)

Fixture: `/var/tmp/sqzrlite-t3-fixture/repo` (same as T3 — 150 files: 120 `.cs`, 30 `.md`), pinned
`SQUEEZR_HOME=/var/tmp/sqzrlite-t5-home`. Box at run time: load 2.04, up 8:39.

- `sqzrlite --version` → `0.1.0+72273917a2ecd54042e842bb09529920353975a6` (stamp + commit survive publish).
- `init` / `add` / `index`: 150/150 files, 960 code symbols, 990 chunks — identical to T3's Debug-run numbers.
- `search` identifier (`LedgerEntry`, top-3): 3 exact matches, `tokensUsed:22`; regex docs probe: 2 matches;
  prefix-scoped probe: 5 matches under `docs/`.
- `map --depth 1`: `docs/ (30 files)`, `src/ (120 files)`, 13 tokens.
- `symbol OrderService42`: signature + full body, `state:"complete"`, 118 tokens — the SEMANTIC path works
  from inside the single file (Roslyn + MSBuildLocator resolve their run-time MSBuild from the machine SDK;
  the BuildHost folders ride next to the binary).
- `refs` / `span` / `pack` / `status`: shapes identical to T3's run; `status` keeps `semantic:"unsupported"`,
  no `embeddings`/`qdrant` keys.
- `serve` over real stdio: `serverInfo.name:"sqzrlite"`, version stamp in the handshake; `tools/list` →
  7 tools (`code_pack`, `code_search`, `expand_span`, `find_references`, `index_status`, `repo_map`,
  `symbol_lookup`); `code_search` answered 2 exact hits; `index_status` → `semantic:"unsupported"`, no
  embeddings/qdrant keys. Serve-to-first-answer 0.263–0.267 s over 3 runs (handshake 0.125–0.128 s).

## Source-level note (no production change made)

`RoslynSession.cs(358)` warns `IL3000` under single-file publish (`Assembly.Location` returns `""` for
embedded assemblies). The site is the default-framework-references helper and already filters
`!string.IsNullOrEmpty(a.Location)`, so under single-file it degrades silently to an empty fallback set
instead of crashing — verified by the green semantic verbs above. Left as-is deliberately: zero source
changes shipped in T5.

## Shipped in this task

- `scripts/publish-sqzrlite.sh` — the pinned shape, both rejection records in the header, `--rid/--compress/--pack`.
- `scripts/install-sqzrlite.sh` — source leg, no Docker/model hints (unlike the full product's `install.sh`).
- `scripts/install-release-sqzrlite.sh` — GitHub Releases leg, `sqzrlite-<rid>.tar.gz` asset, no services steps, no autostart leg.
- `CHANGELOG.md` — the sqzrlite row under `[Unreleased] / Added`.
- Publish output stays in `artifacts/` (gitignored); nothing binary enters the tree.
