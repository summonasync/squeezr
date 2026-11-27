# 130/T3 evidence — sqzrlite front-end

Run date: 2026-11-27. Worktree: `.claude/worktrees/130-sqzrlite`, branch `feat/130-sqzrlite`, head `1d3f822` + T3 changes.
Box: linux-x64, dotnet 10. Everything below ran against the built `sqzrlite.dll` (Debug, net10.0) with a pinned
`SQUEEZR_HOME=/var/tmp/sqzrlite-t3-home` (fresh dir, outside `$HOME`).

## External-service proof

- `pgrep` at run time: `/bin/ollama serve` (pid 6705) and two `./qdrant` processes (pids 6992, 6997) WERE running on
  this box — they belong to the full product's dev setup and were left untouched (no container started, stopped or
  probed by this task).
- The whole lite flow (init → index → 6 query verbs → MCP serve with tool calls) completed with zero requests to
  them: ports 6333/6334/11434 were only listened on by those pre-existing processes; the lite binary never opened a
  network connection.
- Dependency closure proof: `dotnet list src/Squeezr.Lite/Squeezr.Lite.csproj package --include-transitive`
  (`dependency-proof.txt`) contains **0** lines matching Qdrant and **0** matching Ollama. `grep -ri "qdrant|ollama"
  src/Squeezr.Lite` hits only two prose strings: the index_status tool description ("blocks are absent rather than
  zeroed") and the section-name list in `LiteConfigFile` (tolerance warnings). No package, no code reference.

## Fixture and index numbers

- Generated fixture: `/var/tmp/sqzrlite-t3-fixture/repo` — **150 files** (120 `.cs` across 12 namespaces + 30 `.md`).
- `sqzrlite index --json`: `added:150, files:150, ignored:0, skipped:0`, wall **1.52 s** (`time`), zero warnings.
- Store: **960 code symbols**, **990 chunks** (kindChunks `{code:960, docs:30}`).

## Per-tool answers (real runs, `run.log`)

| verb | probe | answer |
|------|-------|--------|
| init | fresh dir | workspace minted, 3 store files created |
| add | `repo --name fixture` | registered |
| index | full pass | 150/150 files, 1.52 s |
| search | `LedgerEntry --top-k 3` | 3 exact matches, summary `tokensUsed:22, exactTokens:22` |
| search | `--regex --kind docs` | 2 docs matches (note010/note015) |
| search | `--prefix docs/` | 5 matches, scoped to the docs subtree |
| map | `--depth 1` | `fixture/` → `docs/ (30 files)`, `src/ (120 files)`, 13 tokens |
| symbol | `OrderService42` | signature + full body, `state:"complete"`, 118 tokens |
| refs | `OrderServiceHelper42` | 2 definitions (class + ctor); `LedgerEntry2` → definition, 0 refs |
| span | `Gen066.cs 6 10` | 5 span lines + 3 before + 3 after, 87 tokens |
| pack | `OrderServiceHelper42 --max-tokens 900` | definition + 6 body lines + callers block, `complete:true` |
| status | `--json` | repos row + kindChunks + `semantic:"unsupported"`, no embeddings/qdrant keys |
| serve | stdio handshake | `serverInfo.name:"sqzrlite"` (`serve-out.ndjson`) |

Probes that name a non-existent symbol answer honestly (exit 1, `workspace_error` with "No symbol named 'X' in the
index", or an empty pack with didYouMean) — those lines are in the first run of `run.log`; the committed rerun uses
existing symbols.

## MCP serve over a real stdio process

Raw JSON-RPC over stdin (`serve-out.ndjson`, stderr in `serve-stderr.log`):

- `initialize` → `serverInfo: {name:"sqzrlite", version:"0.1.0+<sha>"}`, lite-worded instructions naming no services.
- `tools/list` → **7 tools**: code_pack, code_search, expand_span, find_references, index_status, repo_map, symbol_lookup.
- `tools/call code_search {query:"VaultClient", topK:2}` → 2 hits, first `src/Gen004.cs`, `source:"exact"`.
- `tools/call index_status` → `data.semantic:"unsupported"`; `embeddings` and `qdrant` keys **absent**.
- Startup line (stderr): binding line + "no background indexer runs in lite — tools answer from the index as of the
  last 'sqzrlite index' pass".

## Envelope honesty — the semantic shape

Decision recorded on the board log: lite's `index_status` data replaces the full product's `embeddings`/`qdrant`
blocks with a single named state — `"semantic": "unsupported"` — and the other shared envelopes keep the full
product's field set minus semantic fields (nothing zeroed). Comparison against the full `squeezr` binary on the same
fixture (separate home, `--no-semantic` index):

- `search "LedgerEntry" --top-k 3` — match rows **byte-identical** (repo/path/line/score/snippet/source/alsoMatched).
  Summary differs exactly by the declared absence: lite `{exactTokens}` vs full `{exactTokens, semanticTokens:0}`.
- `index_status` — `repos`/`kindChunks` engine data equal in kind; lite carries `semantic:"unsupported"`, full carries
  `embeddings`/`qdrant`/`embeddingModelKey` blocks. The CLI `status` verb's JSON is a lite presentation shape
  (compact), not a byte-comparable contract surface.
- `refs "Scale"` parity check: lite 120 definitions / 0 refs; full squeezr on the same fixture — also 120/0. The
  engines answer identically.

## Tests

- New suites (`tests/Squeezr.Tests/Lite/`): **16/16 green**
  (`dotnet test --filter "FullyQualifiedName~Squeezr.Tests.Lite"`).
  - `LiteServerSmokeTests` (7): server name `sqzrlite`, 7-tool list, uninitialized-workspace structured error with
    `sqzrlite` remedies, index_status semantic-unsupported shape (embeddings/qdrant keys absent), code_search exact
    arm with measured cost baseline, all 7 tools answering, full-profile config serve tolerance warnings.
  - `LiteConfigToleranceTests` (6): full-profile config (embeddings/ollama/qdrant sections) parses without crash and
    warns per section; `null` semantic sections warn once; unknown hand-added section named; missing config file →
    no warnings; Core Load→Save round-trip preserves the semantic sections; `sqzrlite init` writes a config with no
    semantic sections.
  - `LiteCliEndToEndTests` (3): child-process flow init → add → index → search → status → map → symbol → refs → span
    → pack (exit 0 + payload assertions each), index without init refused with the `sqzrlite init` remedy, full-profile
    config index runs with tolerance warnings on stderr.
- Protected suites: `SyncOverAsyncSweepTests` + `ConfigNullSectionCliTests` + `ToolEnvelopeTests` + `JsonOutputTests`
  — **35/35 green**. Ratchet reading: `284 files scanned under src/Squeezr.Core, 5 sites against a floor of 5`
  (Core untouched in this task).
- Build: `dotnet build Squeezr.slnx` — 0 warnings, 0 errors. `dotnet format` on `src/Squeezr.Lite` — no changes.

## Notes for the reviewer

- Probes that named non-existent symbols during the first run (refs RateLimiter66, pack PaymentGateway11 — the
  fixture's symbol names cycle by i % 6) answered honestly: exit 1 with "No symbol named 'X' in the index" /
  empty pack with didYouMean. The committed rerun uses existing symbols.
- Qdrant/Ollama services belonged to the full product's dev environment and were left running untouched; the proof
  of "zero external services" is the empty dependency closure plus a complete lite run that makes no network calls.
