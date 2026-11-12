```
            ▄████████▄▄▄                     [{((<             ·            >))}]
      ▄▄  ▄██████████████▄▄                      [{((          ·         ))}]
     ███████████████████████▄                        [{(       ·      )}]
    ██████████████████████████▄                          [{    ·   }]
    ▀██████▒▒▒▒▒▒▒██████████████▄                            [  · ]
     ▀█████▒▒▒▒▒▒▒▒▒██████████████▄                            ;
       ▀███▒▒▒▒▒▒▒▒▒▒▒█████████████▄
        ████▒▒▒▒▒▒▒▒▒▒▒█████████████▄
        ▀█████▒▒▒▒▒▒▒▒▒▒█████████████▄
         ███████▒▒▒▒▒▒▒▒██████████████
          █████████▒▒▒▒████████████████
           ████████████████████████████
            ████████████████████████████▄
             ▀████████████████████████████
               ▀███████████████████████████
                 ▀████████████████████████▀
                   ▀█████████████████▀▀██▀
                      ▀████████████▀
                          ▀▀▀▀▀▀▀▀

███████╗ ██████╗ ██╗   ██╗███████╗███████╗███████╗██████╗ 
██╔════╝██╔═══██╗██║   ██║██╔════╝██╔════╝╚══███╔╝██╔══██╗
███████╗██║   ██║██║   ██║█████╗  █████╗    ███╔╝ ██████╔╝
╚════██║██║▄▄ ██║██║   ██║██╔══╝  ██╔══╝   ███╔╝  ██╔══██╗
███████║╚██████╔╝╚██████╔╝███████╗███████╗███████╗██║  ██║
╚══════╝ ╚══▀▀═╝  ╚═════╝ ╚══════╝╚══════╝╚══════╝╚═╝  ╚═╝
```

**Squeezr — maximum meaning per token.**  
*The eye that lets the horde find things without reading everything.*

`await SummonAsync();` — part of the summoner's kit  
overmind · grimoire · **▸ squeezr** · mindlash studio

---

## What it is

Squeezr is a local code indexer for AI agents. It squeezes maximum signal out
of your codebase into a minimum of LLM tokens: a hybrid exact + semantic index,
served to agents as native MCP tools, so they navigate by map, symbol,
references and spans instead of reading whole files. One .NET binary. Everything
stays on your machine — the Docker pieces (Qdrant, Ollama) are optional infra
for the semantic arm, managed by a single command.

## Why it exists

Anyone who has run an LLM agent over a real repository knows this pain: the
agent starts "exploring the codebase", reads whole files, burns 30–80k tokens
on grep navigation — before writing a single line. The context window dies,
quality drops, the API bill goes up.

We looked at that and asked: why should an agent read whole files at all?
People don't. People look at the project **map**, find a **symbol**, check who
**calls** it, and open the exact **span**. That is what an agent should get —
just in machine form.

So Squeezr was born. The name is from *squeeze*: squeeze the juice out of your
code into a token budget. 🍋

## Lineage

Nothing here was invented from scratch — it is the best of prior art, rebuilt
local-first:

- **[Zoekt](https://github.com/sourcegraph/zoekt)** and Russ Cox's legendary
  [trigram code-search series](https://swtch.com/~rsc/regexp/regexp4.html) —
  our trigram engine follows these designs. Substring and regex search over
  millions of lines in milliseconds, no external processes.
- **ripgrep** — the reference for fast, honest search. Smart-case in `search`
  is its idea.
- **ctags / LSP** — the symbol-navigation idea; here the symbols are extracted
  by Roslyn with compiler-grade accuracy.
- **[Model Context Protocol](https://modelcontextprotocol.io)** — agents get
  the index as native MCP tools, not "run grep and pray".

## Quickstart

Install (self-contained, no .NET runtime needed):

```bash
# Linux
scripts/install.sh
```

```powershell
# Windows: from source (needs the .NET 10 SDK)
powershell -File scripts\install.ps1
```

On Windows there is a second path — a ready installer `SqueezrSetup-<version>-win-x64.exe`:
double-click, no admin, registered in "Apps & features". It is built locally with
one command, see [Windows: the `.exe` installer](#windows-the-exe-installer).

Both scripts declare `#Requires -Version 5.1`, so they run in stock Windows
PowerShell (`powershell -File ...`) and in pwsh 7 alike — no separate pwsh install.

Or just build for development (needs the .NET 10 SDK): `dotnet build Squeezr.slnx`.

Then, identically on both platforms:

```bash
# create a workspace at your project root (the project itself gets ZERO
# squeezr files — manifest, config and index live in the central store ~/.squeezr/)
squeezr init --name my-project
squeezr add /path/to/repo

# bring up the infrastructure (Qdrant + Ollama in Docker, see the services section)
squeezr services up

# index and search
squeezr index          # live progress by phase; --reembed for a full re-embed
squeezr search "WorkspaceManager"            # hybrid: exact + semantic (RRF)
squeezr search "w.rkspace" --regex --max-tokens 200
squeezr search "debounce" --kind code        # code/docs filter, applies to both arms
squeezr refs Slugify                          # who calls a symbol (C#)
squeezr status                                # includes Qdrant state
squeezr map --depth 2                         # repo map: signatures, no bodies
squeezr symbol WorkspaceManager               # exact symbol lookup
squeezr expand src/Foo.cs 42 87               # expand a span from the live file
squeezr compact                               # trigram index compaction
squeezr vectors reconcile                     # Qdrant points vs meta.db: dry-run, --apply cleans orphans
squeezr drop                                  # remove the workspace index entirely
squeezr workspace list                        # workspace registry from the central store
```

Machine-readable output (`--json`): `status --json` — one JSON object
(camelCase, including `staleFiles`/`indexAge`); `search --json` and
`workspace list --json` — NDJSON (one object per line, flushed per line; for
search the final line is `{"type":"summary"}`). Errors in JSON mode —
`{"error":{"code","message"}}` on stderr + a non-zero exit code, codes are
stable (`workspace_not_initialized`, `index_not_built`, `invalid_query`, ...).
Human-readable output does not change.

Progress events (`--events`): `squeezr index --events` and
`squeezr serve --watch --events` stream NDJSON indexing events — one per line,
flushed per line: `started` (workspace; `provider?` — the resolved embedding
provider, only on `index` runs with semantics; watch passes also carry
`changedPaths`) → `fileIndexed` (`{repo, path, files}` — cumulative counter for
the run) → `progress` (`{repo, phase, total}` for the semantic/embeddings/trigram
phases) → `completed` (`durationMs`, `files/added/modified/deleted` totals,
`symbols?`, `provider?`, deferred point-cleanup counters
`deferredReaps`/`pendingReaps`, per-repo breakdown) or a terminal `error`
(`code`, `message`). Transport: `index` — stdout, `serve --watch` — stderr
(stdout carries MCP JSON-RPC). A quiet incremental run emits exactly
`started → fileIndexed×N → completed`. Composition with `--json`: first the
completed event sequence, then the final `--json` object; errors are duplicated
to the 035 envelope on stderr + exit 1. A busy workspace registry (concurrent
write to `workspaces.json`) fails the run before it starts: the stream opens
with a terminal `error` without `started`, code `workspace_registry_busy` — a
"retry in a moment" signal for scripts; the same code arrives in the
`--json`/stderr envelopes of every command touching the registry.

Embeddings — provider from `auto`/`ollama`/`openai`, chosen by the
workspace config's `embeddings` section (it lives in the store:
`~/.squeezr/workspaces/<storeKey>/config.json`; easier to edit via `squeezr tui` → Config):

| Provider | When to pick it | Config |
|---|---|---|
| `auto` (default) | GPU with zero setup: on every indexing run ollama answers the probe — count on GPU through it; no answer — the run fails with an explicit error hinting `squeezr services up` | `provider: "auto"` (written by `init` by default) |
| `ollama` | Noticeably faster on large repos; on AMD GPUs it computes via ROCm (see the services section) | `embeddings.provider: "ollama"` + an `embeddings.ollama` section: endpoint `http://127.0.0.1:11434` (IPv4 literal — a native Windows Ollama listens there, while `localhost` can resolve to a refused `::1`), model `nomic-embed-text` (768d, profile `nomic-embed-text-v1.5` — the `search_document:`/`search_query:` prefixes are added by squeezr itself). Native `/api/embed`, no API key. The model is pulled automatically at first indexing (auto-pull); ollama not running — an explicit error hinting `squeezr services up` |
| `openai` | OpenAI-compatible endpoint (OpenAI, Azure, vLLM) | An `embeddings.openAiCompatible` section + the key only via env `SQUEEZR_API_KEY` |

No model downloads — the ollama provider pulls its model itself, the
openai-compatible endpoint serves whatever model you point it at. A provider
or model switch changes the chunk key — affected chunks are re-embedded
automatically; on an existing workspace a switch that changes the vector size
honestly complains "collection vector size 768 ≠ 1536" — cured by
`squeezr index --reembed`, silently overwriting the collection is impossible.
Qdrant unavailable — indexing and search degrade softly to the exact layer,
nothing crashes.

## For AI agents

An AI agent told to "use squeezr here" gets productive in one conversation.
Run this only on an explicit human request — never install squeezr
proactively. The full ritual with guardrails, tool cheat-sheet and
troubleshooting lives in [`skills/squeezr/SKILL.md`](skills/squeezr/SKILL.md);
short version:

```bash
curl -fsSL http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr/raw/main/scripts/install-release.sh | bash
squeezr services up
squeezr init && squeezr add . && squeezr index --events
squeezr mcp install --all --scope user --yes
```

1. The installer resolves the latest published Gitea release, unpacks it into
   `~/apps/squeezr/<tag>/` and repoints the `~/.local/bin/squeezr` symlink (if
   that dir is not on PATH, the script says so — fix the shell profile and open
   a new terminal). Pin or roll back with `--version <tag>`. **Linux only** —
   releases are published for `linux-x64` alone. On Windows, build the setup
   executable locally (`powershell -File scripts\build-installer.ps1`) or
   install from source with `powershell -File scripts\install.ps1`.
2. Starts the managed Qdrant + Ollama stack; the first run pulls ~3.6 GB of
   images. If docker is missing, stop and ask the human — do not improvise.
3. Creates a workspace in the current directory and indexes everything (zero
   files land in the project).
4. Registers the MCP server in every detected client at user scope,
   non-interactive. Restart the agent session afterwards — MCP clients read
   configs at startup.

From then on navigate via the six tools — `repo_map`, `code_search`,
`symbol_lookup`, `find_references`, `expand_span`, `index_status` — instead of
reading whole files.

The skill content lives in this repo at `skills/squeezr/` and imports into
Overmind as the kit «squeezr» (phase 1: manual kit import through the Overmind
UI), so agent teams can onboard without reading this README.

## Trade-offs

Read these before you adopt. They are the same weight as the feature list —
that is the deal.

- **The index has a build cost up front**: CPU, disk, and a warm-up pass per
  repo.
- **The semantic arm needs Qdrant + an embedding provider** (Ollama or
  equivalent). Exact search alone is useful; semantic is not free.
- **C# is first class.** Roslyn-grade symbol precision means the matrix is
  honest: C# gets symbols, signatures, doc-comments and real callers; JS/TS/TSX
  get a tree-sitter symbol layer — functions, classes, interfaces, type
  aliases, import graph; Rust gets that same layer over its own vocabulary —
  functions, structs, enums, unions, traits, impl blocks, modules, type
  aliases, consts, statics and macro definitions, with `use` trees, calls,
  trait/impl targets and macro invocations as its reference graph; Python gets
  it over functions, async functions, classes, methods, module and class
  constants, with imports (plain, aliased, `from`, relative, wildcard), calls
  and base classes as its reference graph — and a dotted `import a.b.c` binds
  the top package `a`, never the leaf; Go gets it over functions, methods
  named by their receiver (`Store.Put`), structs, interfaces, type aliases,
  type declarations and package-level consts and vars, with imports, calls,
  type uses and embedded fields as its reference graph — and `import
  "net/http"` binds the last element `http`, so the two grammars keep their
  own import rule instead of sharing one; every other
  text (Markdown, YAML, JSON…) is indexed by a sliding-window fallback chunker
  — searchable, but not symbol-aware.
- **Per-language reference pools, because the same name is not the same
  thing.** `mul` in JavaScript and `mul` in Rust are unrelated symbols, so a
  reference edge is name-matched only inside its own language pool — js/ts/tsx
  share one, and rust, python and go each got their own. No language is
  declared a symbol source before its own vocabulary lands in the index home
  (`TreeSitterLanguages`), and the refusals are stated rather than smoothed
  over: a go blank identifier and a multi-name const/var spec name nothing, and
  rust, python and go capture no doc comments yet — js/ts keep theirs.
- **Adoption is the hard part, not the tech.** Agents must be *allowed* to
  call it. Tool allowlists and flow gates decide whether the index gets used
  at all.
- **A hot index on every session burns background CPU** whether or not the
  agent ends up querying it.

## Central workspace store

Squeezr does not litter your project: `squeezr init` creates zero files in the
current folder — no `.squeezr/`, no manifest, no compose. Everything lives in
the central store:

```
~/.squeezr/                          # the store (override: env SQUEEZR_HOME)
├── workspaces.json               # registry: name/rootPath/storeKey/createdAt/lastUsedAt
├── workspaces.lock                  # registry lock — one writer to workspaces.json at a time
├── workspaces/<storeKey>/          # one workspace's index store
│   ├── workspace.json               # manifest (repositories)
│   ├── config.json                  # config (embeddings, watch, ...)
│   ├── meta.db                      # LiteDB
│   ├── trigram.idx                  # trigram index
│   ├── index.lock
│   ├── watch.owner                  # watch-loop claim: pid+host+kind, exclusive creation; a foreign host
│   │                                # is outlived only by time (×4 stale windows), not by pid
│   ├── watch.heartbeat              # per-kind heartbeat: written by the process running the watch loop (never
│   │                                # by an observer) — kind `watch`, the terminal daemon
│   ├── serve-watch.heartbeat        # ...or `serve --watch` from an MCP client, when it is the owner
│   ├── beats/                       # per-pid beats of every live watch-role process: {kind}.{pid}.beat, same
│   │                                # JSON {pid, kind, startedAt, lastBeatAt, host} plus an additive activity
│   │                                # member while a pass runs; the roster fleet enumerates
│   ├── queries.jsonl                # query telemetry for `stats` (local; drop removes it)
│   └── queries.lock                 # serializes writers to queries.jsonl: appends, scrub on compact and
│                                    # stats --reset (writers wait for the budget; reset refuses while busy,
│                                    # removing nothing)
└── infra/docker-compose.yml        # qdrant + ollama (generated by `squeezr services up`)
```

- **Discovery like git:** any command run from any subdirectory of a workspace
  finds it on its own (search up the registry). "Opened it in the wrong folder"
  disappeared as a class of problem.
- **Registry:** `squeezr workspace list` shows all workspaces from
  `workspaces.json`; entries whose rootPath vanished from disk are marked
  `stale` — removed one by one via `squeezr drop --store-key <key>` or in bulk
  via `squeezr workspace repair --dry-run` / `--yes` (with store dir sizes).
  Entries with rootPath = `$HOME` or the store parent (incident heritage) are
  marked `husk` with the same drop hint.
- **storeKey shape:** `<name slug>-<first 8 hex of SHA1 over the canonical
  rootPath>` (slug: lowercase, letters/digits, separators folded to `-`; empty
  name → `workspace`). The key is always exactly one segment inside
  `workspaces/`; `drop --store-key` and the repair prune sweep refuse keys of
  any other shape (`.`, `..`, separators, NUL, colon — on both platforms)
  before the first filesystem touch: the refusal reads and deletes nothing.
- **Orphans and repair:** if a store dir outlived its registry (crash, manual
  `workspaces.json` cleanup), `workspace list` shows it as
  `orphan — adoptable` (manifest with rootPath, key matches, root alive),
  `orphan — needs root` (old manifest without rootPath) or `orphan — root gone`
  (rootPath recorded but the directory is not on disk). `squeezr workspace
  repair` returns only live orphans to the registry: adoption requires a live
  root — both in the batch route (liveness resolved in one scan) and in the
  explicit `--store-key <key> --root <path>` act (the path is checked on disk
  at the moment of writing the row, the hash of name+root must match the key,
  a foreign store cannot be smuggled in). A dead root is a dead end, not work:
  such a store stays a `needs root` row with reason `root-gone`, is never
  adopted and never swept (a row without a registry line is untouched by the
  primitive `drop`); the only route out is restoring the directory at the
  recorded path and re-running repair. Bulk cleanup of stale rows requires
  explicit confirmation: `--dry-run` only lists, `--yes` or a TTY answer
  confirms; without a terminal and without `--yes` repair refuses (a missing
  prompt is not consent). Deletion goes through the same single path as `drop`
  (row + store dir); adoptable orphans and live entries are not deleted; a
  dated copy of `workspaces.json` is written before any real cleanup. Fallback
  path: `squeezr init` in the project root — on top of an existing store dir it
  works as lossless adoption: a live `config.json`, the manifest (repo list)
  and the index (`meta.db`) survive; no reset to defaults.

```
$ squeezr workspace list
Squeezr (orphan)   unknown — needs --root   squeezr-d23cb388   orphan — needs root
warning: workspace store 'squeezr-d23cb388' has no recorded root path; run 'squeezr workspace repair --store-key squeezr-d23cb388 --root <path>'.

$ squeezr workspace repair --store-key squeezr-d23cb388 --root REDACTED-PATH/Projects/AI-Tools/Squeezr
Adopted workspace Squeezr (squeezr-d23cb388) with root REDACTED-PATH/Projects/AI-Tools/Squeezr
```

- **Environment variables:** `SQUEEZR_HOME` relocates the whole store,
  `SQUEEZR_CLIENT_HOME` relocates the directory where `squeezr
  mcp/install/remove/doctor` looks for agent client configs (user profile by
  default). The second exists mostly for tests and sandboxes: on Windows
  `GetFolderPath(UserProfile)` goes through the shell API for known folders and
  ignores `USERPROFILE`, so otherwise the profile cannot be redirected.
  Unset — behavior is exactly as before.
- **Legacy migration is automatic:** projects on the old layout (`.squeezr/` +
  `squeezr.workspace.json` in the root) move to the store on first touch — the
  contents of `.squeezr/` and the manifest are carried over, the
  squeezr-generated `docker-compose.yml` and the dead `state.json` are removed.
  Details: [docs/migration-central-store.md](docs/migration-central-store.md).
  Qdrant collections (named after the workspace path, not the store) survive
  the migration, no vectors are lost.

## Cross-repo workspaces

A workspace is not limited to one repository — register sibling repos and
search them as a single index:

```bash
squeezr add ../other-repo --name other-repo
```

The path is absolute or relative (resolved against the workspace root);
`--name` sets the repo name the tools filter on — without it, the directory
name is taken.

- One workspace, many repos: `squeezr index` covers them all, and the watch
  daemon keeps each one fresh.
- Per-repo filtering: the `repo` parameter of `repo_map` / `code_search` /
  `symbol_lookup` / `find_references` (CLI twins: `--repo` on `search` and
  `map`).
- Kind filtering: the `kind` parameter of `code_search` (CLI twin: `--kind` on
  `search`) selects what a hit can be — `all` (default, unchanged behavior),
  `code` (program sources) or `docs` (markdown/text/json/yaml/toml/xml prose
  and config). The filter runs on both hybrid arms: a Qdrant payload filter on
  the indexed `kind` field on the semantic side, a meta.db file-kind join on
  the trigram/exact side. Unknown values are rejected with the allowed list,
  never silently treated as `all`. Points without a `kind` payload (orphans,
  or points written by pre-057 binaries until the backfill converges them)
  match only `kind=all`.
- Weak-match honesty: when a hybrid query gets no exact-arm support (neither
  the phrase arm nor the identifier leg) and the top semantic score sits below
  the embedding model's relevance band, `code_search` and `search` still return
  the hits but the envelope carries a warning naming the narrowing moves —
  `narrow with kind=code or prefix` — so low-confidence prose matches never
  pose as a confident answer. Strong answers stay warning-free.
- Subtree scoping: the `prefix` parameter of `repo_map` / `code_search` (CLI
  twins: `--prefix` on `map` and `search`) answers only inside a directory
  subtree, matched against stored relative paths. An empty subtree is an error
  naming the nearest directories, not a silent empty answer.
- Ranking curation: `indexing.rankExclude` in the workspace config
  (`["audits/**", "BACKLOG/**"]`) keeps doc trees out of `code_search` ranking
  — both arms — without unindexing them: `expand_span` / `find_references` /
  `symbol_lookup` still reach every excluded file, and no reindex is needed
  (query-time only). `prefix` narrows in, `rankExclude` pushes out, the two
  intersect. `index_status` lists the active patterns in `warnings`,
  `squeezr status` prints them next to the embeddings provider; a malformed
  glob produces a warning and is skipped instead of breaking the query. A value
  with a broken shape (a bare string instead of the array, non-string entries)
  no longer takes the whole config down: the string is salvaged as a
  one-pattern list, junk entries are dropped, and `squeezr status` still prints
  the surviving patterns. Drive-letter patterns (`C:/repo/**`, `C:\repo\**`)
  are rejected with a warning — patterns are repository-relative, so a drive
  prefix can never match anything.
- Query globs (`indexing.rankExclude`, MCP/CLI `pathGlob`) read the same
  classes as the ignore dialect — `[abc]`, `[a-z]`, `[!…]`/`[^…]`, `?`, `**` —
  and since 063/T14 they match there too. They differ in two places: there `\`
  stays a directory separator (`src\**\*.cs`), not an escape, and nothing
  guards residual shapes — an unterminated `[` or a POSIX `[:alpha:]` reads as
  literal characters and a plain member set instead of being skipped with a
  warning the way the ignore layer does. The separator asymmetry also means a
  query class cannot contain `/` — the splitter has no bracket awareness. So
  `**/[Bb]in/**` really excludes `src/Bin/…`, while a glob written to name
  literal brackets (`audits/[v1]/**`) no longer names that directory — it reads
  as a one-character set over `v`/`1`.
- Coverage is visible before you query: `index_status` and `squeezr status`
  show per-repo file/symbol/chunk counts; `embeddings.coverage` in
  `index_status` adds the semantic share (embedded / total chunks).

Discovery stays per-directory: a project resolved from inside its tree keeps
its own workspace — sibling repos join only when deliberately added with
`add`.

## Indexing exclusions

Squeezr does not index everything: built-in defaults cut `node_modules`, `bin`,
`obj`, `dist`, `.next`, `.vs`, `coverage` and other noise. On top of the
defaults — three layers, each next one overrides the previous
(last-match-wins):

1. **`indexing.ignore`** in the workspace config
   (`~/.squeezr/workspaces/<storeKey>/config.json`) — globs you always want
   ignored.
2. **`.squeezrignore`** in the repo root — gitignore syntax, NOT cascading
   (root file only).
3. **The `.gitignore` cascade** — any `.gitignore` along the path to a file,
   patterns relative to that file's directory. Enabled by default.

The `indexing.respectGitignore` flag (bool, default `true` in
`IndexingConfig`) switches the whole `.gitignore` layer off. Old configs
without the key load as `true`, no migration needed.

Supported gitignore subset: empty lines, `#` comments, `!` negation, a
leading/inner `/` = anchor to the directory, a trailing `/` = directories
only, `*`, `?`, `**`, character classes — sets `[abc]`, ranges `[a-z]`,
negation `[!…]` and `[^…]` — and `\` escapes that strip the magic from
`* ? [ ]` and make a literal of space, `#`, `!` (`Generated\ Files/` reads as
`Generated Files/`). Since 063/T14 these forms match instead of being dropped:
<removed 2026-10-05 — 063/T14>~~character classes and `\` escapes are NOT
supported~~</removed>. Only residual shapes are dropped with a warning — an
unterminated `[`, POSIX classes like `[:alpha:]` (and nested inside a set:
`[a[:digit:]b]` — the whole pattern is skipped, not git-resolvable), an
escaped separator `\/`, a `\ `-escape of a trailing space (the line is trimmed
before validation, so `Generated\ Files` works but `foo\ ` does not) and a
dangling `\` at line end: `unsupported ignore pattern '<raw>' in <file>;
pattern skipped.` `<raw>` is the file line as it stands, original spaces
included. Character classes and escapes in `indexing.secretGuardExtra` widen
the mask but never narrow it: a `!` negation still cannot pass into the
compiled extras.

Observability: `squeezr index` prints `ignored N file(s)` next to
skipped/added/modified. `squeezr status --json` and MCP `index_status` carry an
`ignored: { files, bySource: { defaults|config|squeezrignore|gitignore } }`
block per repo (zero-count keys are absent — token economy); `index
--json`/`--events` carry the same numbers as flat fields `ignored` (int) +
`ignoredBySource` (keys — the same dictionary as `ignored.bySource`). The unit
is files: a directory cut by a rule (`out/`) counts not as 1 but as every file
inside it (counted by name, contents unread). The numbers are a snapshot of
the last indexing pass (not the current disk state): deleting/moving a file on
disk does not change `ignored` until the next `squeezr index`. When
exclusions exist, a warning names the dominant source and how to disable/fix
it. Silent filter-out is forbidden: `ignored > 0` is always visible. A
workspace indexed before this field existed shows `ignored` absent/0 without
errors — an honest absence of data, not fake zeros.

Removal happens on the next pass: a file that landed in `.gitignore` after
indexing disappears on the next `squeezr index` (or watch pass).

The read path (`code_search`/`symbol_lookup`/`repo_map`/`find_references`,
inline refresh of a stale file) does not walk the tree: the `.gitignore`
cascade is collected only over the ancestors of that file's path, and the
decisions are identical to the full cascade.

**Breaking change (035 --json contract):** `RepoIndexResult` gained a 7th
position `Ignored` (int, total count) and an 8th `IgnoredBySource`
(`Dictionary<string,int>?`, keys `defaults|config|squeezrignore|gitignore`,
`null`/absent — nothing excluded). In the `--json` output of `index`/`events`,
every `repos[]` element now carries an `ignored` field (int) and an additive
`ignoredBySource` (an object with string keys — the same value dictionary as
`ignored.bySource` in status). In `status --json` and `index_status` there is
an `ignored: { files, bySource }` block per repo. Existing fields are neither
renamed nor removed — additive only.

## Secret guard: what it refuses, and how to clean an old store

The guard decides **by path shape**: to classify a file it never opens it —
the classifier contains zero filesystem probes
(`SecretGuardTests.Classifier_SourceHasNoFileSystemProbe`). It never sees
secret values and never writes them anywhere: not to reports, not to `--json`,
not to telemetry. Protected path names do not appear in pass or cleanup
reports either — counters only. The name does sound on reads, and differently
there: a **refusal** returns the path the caller named itself, while a
**withheld line** names the first result the guard removed — the caller may
never have named it at all (see below).

**The built-in set — 19 masks.** `SecretGuard.BuiltInPatterns`, the list is
frozen by test `BuiltInPatterns_AreTheFrozenSpecList`:

`**/.env`, `**/.env.*`, `**/*.key`, `**/*.pem`, `**/*.pfx`, `**/*.p12`,
`**/*.jks`, `**/*.kdbx`, `**/id_rsa*`, `**/id_dsa*`, `**/id_ecdsa*`,
`**/id_ed25519*`, `**/.npmrc`, `**/.netrc`, `**/.git-credentials`,
`**/.pgpass`, `**/secrets.json`, `**/secrets.*.json`, `**/*.secret`

The single exception — conventional stubs `.env.example` / `.env.sample` /
`.env.template`, and only for the `.env` family: a name that is formally a
stub but shape-matches the rest of the set (`.env.key`) is protected anyway.

**The built-in set cannot be narrowed.** The only configuration knob is
`indexing.secretGuardExtra` (a glob array in the workspace `config.json`), and
it is **union-only**: user masks are checked first, the built-ins always
apply, nothing subtracts from them. A shape that names nothing does not
compile into `secretGuardExtra` at all — a negation `!`, a comment, dir-only
(`secrets/`) and residual unsupported forms are **inert by construction**, not
"filtered later"
(`SecretGuardTests.SecretGuardExtra_ClassesAndEscapesWidenOnly_NegationStaysInert`,
`SecretGuardExtra_CannotNameAFileShape_NeitherWidensNorNarrows`). Character
classes and `\` escapes only ever widen the mask. Empty `indexing.ignore` and
`secretGuardExtra` narrow nothing (`EmptyIgnoreAndEmptyExtra_NarrowNothing`),
and the built-in masks are not part of the editable ignore defaults
(`BuiltInPatterns_AreNotPartOfTheEditableIgnoreDefaults`) — they cannot be
removed through the ignore config either.

**The `secretGuardExtra` dialect is the same as `indexing.ignore`** (the
gitignore subset, see "Indexing exclusions"): an unanchored form matches **at
any depth** — `*.tokens` covers both `root.tokens` and `src/deep.tokens`; an
inner `/` anchors from the repo root — `deploy/*.crt` covers
`deploy/server.crt` but not `deploy/sub/deep.crt`; `**/tokens.txt` — any
depth, but not `tokens.txt.bak`. The meaning matches the ignore layer
verbatim; the row of checks is pinned by test
`SecretGuardExtra_MeansWhatIndexingIgnoreMeans`.

**Two known boundaries — documented limitations, not silence:**

1. **A path whose `.env` is a directory is not protected.** The classifier
   decides by the **last segment**: `.env/dev`, `.env/local.json`,
   `vault/token.key/readme.md` do not count as protected
   (`DirectoryShapedSecret_LastSegmentDecides_NotProtected`). A file with a
   secret name *inside* such a directory is protected by its own mask
   (`.env/prod.key` — yes). A trailing separator is trimmed: a request for
   `.env/` is a refusal, not "not found".
2. **`id_rsa*` matches public keys too.** `.ssh/id_rsa.pub` falls under the
   mask together with the private key — the public part is not indexed and not
   served either. A deliberate price: over-covering with a mask is cheaper
   than leaking a private key.

**What a refusal looks like to an agent.** An explicit request for a protected
path is an error, not an empty answer:

```
'.env' is protected by squeezr's built-in secret guard: never indexed, never rendered
```

The name in a **refusal** is the requested path (plus, for `find_references`:
if all declarations of a symbol are protected, the refusal names the file of
the **first** one — not "the only", just the first in order; staying silent
about "not found" when the file exists would be a lie).

Silently dropped results are counted in `warnings`, one phrase, identical in
every surface (CLI stderr, MCP envelope, TUI — the wording never drifts):

```
warning: secret guard: 3 result(s) withheld ('.env' matches built-in patterns)
```

Attention to the reader: the name in that line is **not what you typed**, but
the first result the guard removed. `repo_map` with prefix `deploy/` names a
file under that prefix; a query without a prefix names a protected path that
appeared in none of your arguments. So the warning line is "safe for
screenshots" exactly to the same degree as the file's contents: not safe. The
decision source is `built-in patterns` or `indexing.secretGuardExtra`; a
refusal always says *built-in*, even when your extras mask decided — only the
tally distinguishes the sources. The same dictionary in ingestion:
`secret guard: 3 file(s) withheld from indexing (2 built-in patterns, 1
indexing.secretGuardExtra)`. If `config.json` failed to read, the built-in
masks stay, the user masks switch off, and this is said separately: `could not
read indexing config (…); user secret-guard masks are inactive`.

**The counters — where to find them.** `index --json` carries
`guardWithheld` / `guardWithheldBuiltIns` / `guardDropped` at the root and the
same three in each `repos[]`; in `--events` the root `completed` —
`guardWithheld` and `guardDropped` (the third lives only in `repos[]`);
`squeezr status` gives `Withheld`/`Dropped` columns plus a summary line;
`status --json` and MCP `index_status` — a
`guard { withheld, withheldByBuiltIns, withheldByConfig, dropped }` object per
repo. The numbers are a snapshot of the last pass, not live disk state. A
guard refusal counts as skipped; the pass does not fail because of it.

**What an upgrade does — and what it does not.** Installing a new binary
**does not remove what was already indexed**: the central store
(`~/.squeezr/workspaces/<storeKey>/`) is untouched by a reinstall, and in an
old `meta.db`, `trigram.idx` and `queries.jsonl` the rows and names of
protected files lie as they lay. From the moment of installation only read-path
behavior changes — those rows are no longer served. The data leaves in one of
two moments:

- **the next indexing pass** (`squeezr index`, including a background watch
  pass): protected paths do not enter the walk, so their rows in the store
  count as removed and leave through the same cascade (symbols, chunks,
  references, the vector-cleanup queue). Names leave the file rows and the
  **new** reap-journal entries (new ones are written without a name); a
  `pending_reaps` row left by the previous build keeps its name through this
  pass — it loses it only in `squeezr compact`;
- **`squeezr compact`** — the explicit route and the only operator command
  that cleans the whole store: the guard purges the remaining protected rows,
  **redacts names in the reap journal**, **scrubs parameter values in
  `queries.jsonl`** (including repo-qualified and absolute forms the product
  itself prints), rewrites `trigram.idx`, rebuilds `meta.db` and returns the
  freed bytes.

There is no `--purge` flag on `compact`: guard cleanup always runs as part of
compaction, and without an operator request nothing is cleaned. The report is
numbers only:

```
secret guard: 7 path(s) purged, 186 row(s) removed · 1 reap journal name(s) redacted · 28 recorded parameter value(s) redacted · 7 point cleanup(s) queued
```

Points a pass could not remove itself are finished by `squeezr vectors
reconcile --apply`. The raw bytes of the cleaned store were verified with a
sweep over `meta.db`/`trigram.idx`/telemetry — evidence in
`audits/store-purge-2026-10-05.md`.

## Managed services: `squeezr services`

Docker infrastructure — qdrant and ollama — is managed by one command; you
never touch compose by hand:

```bash
squeezr services up       # generate the compose file and start both services
squeezr services status   # container table (+ --json, rocm, compose version)
squeezr services down     # stop (data in volumes is NOT removed)
```

- The compose file — `~/.squeezr/infra/docker-compose.yml`, regenerated on
  every `up` (first line: the `# managed by squeezr` marker); project
  `squeezr`.
- Services: **qdrant** (`qdrant/qdrant:latest`, 6333/6334, volume
  `squeezr-qdrant`) and **ollama** (`ollama/ollama:latest`, 11434, volume
  `squeezr-ollama`), restart `unless-stopped`.
- **AMD GPU (ROCm):** if the host exposes `/dev/kfd` and `/dev/dri` (e.g. a
  Radeon RX 7900 XTX), ollama gets those devices passed through and computes
  on GPU; without them — CPU mode.
- The command is global: it works from any directory, no workspace needed.
  Without Docker — a clear error pointing at installation.

**First run — be ready for a download.** The first `services up` pulls images
from the registry: qdrant ~70 MB, ollama ~3.6 GB — minutes on an average
network, tens of minutes on a slow one. The command shows the stages
(`Pulling images…`, `Starting containers…`; raw docker output under
`--verbose`). There is no download timeout — if it stalls or you get bored,
Ctrl+C: the compose process is stopped, the exit is clean. Downloaded layers
are cached — resume with visible progress and retry:

```bash
docker compose -f ~/.squeezr/infra/docker-compose.yml -p squeezr pull   # resume, layers continue
squeezr services up                                                     # up again
```

Repeat `up` visits the registry only for missing images
(`pull --policy missing`, compose v2.24+; on v2.21–2.23 — a full pull) — an
offline restart with images already downloaded works. Requirements: Docker
Engine + compose plugin v2.21+ (`status` uses `ps --format json`); standalone
`docker-compose` also supported. `down` stops the containers but **never**
touches the volumes — qdrant/ollama data survives down/up cycles.

The install lays down a single binary: on Linux — `~/.local/share/squeezr` +
symlink `~/.local/bin/squeezr` (remove: `scripts/uninstall.sh`), on Windows —
`squeezr.exe` in `%LOCALAPPDATA%\Programs\Squeezr` + user PATH (remove:
`powershell -File scripts\uninstall.ps1`). Both installers check for Docker
themselves and hint if it is missing — it will be needed for `squeezr services
up` (Qdrant and, optionally, Ollama). Note: neither install nor uninstall
touches the `~/.squeezr` store — workspaces and models survive a binary
reinstall.

### Windows: the `.exe` installer

Besides the script route, Windows has a real installer —
`SqueezrSetup-<version>-win-x64.exe`. It installs **without admin rights**
into `%LOCALAPPDATA%\Programs\Squeezr` (the same directory `install.ps1` uses,
so one cleanly overlays the other), adds the directory to user PATH itself and
registers in "Apps & features" — uninstall goes from there, not from hunting
a script in a forgotten checkout. Run with elevation, it offers a
per-machine install into `%ProgramFiles%\Squeezr`.

No ready Windows builds exist anywhere and none will: Gitea releases are built
for `linux-x64` only, there is no Windows runner. The package is built
locally, on any Windows machine:

```powershell
# prerequisite: Inno Setup 6 (plus the .NET 10 SDK)
winget install -e --id JRSoftware.InnoSetup

powershell -File scripts\build-installer.ps1
# → artifacts\installer\SqueezrSetup-1.0.2-win-x64.exe
```

Flags: `-Rid win-x64|win-arm64` (default `win-x64`), `-Version <string>`
(default taken from `<Version>` in
`src\Squeezr.Cli\Squeezr.Cli.csproj`), `-SkipBuild` (package the already
published `artifacts\publish` tree), `-IsccPath <path>` (explicit compiler),
`-OutputDir <path>`, `-InstallInno` (deliver Inno Setup via winget if
missing). Inno is found on PATH, in HKCU/HKLM uninstall keys and in the
standard directories — a per-user install in
`%LOCALAPPDATA%\Programs\Inno Setup 6` is found just like a machine-wide one.

Worth knowing:

- **The package is unsigned** — there is no certificate. On first run
  SmartScreen shows "Windows protected your PC": *More info* → *Run anyway*.
- **PATH is picked up only in a new terminal** — the installer broadcasts
  `WM_SETTINGCHANGE`, but already-open consoles do not re-read it.
- **Before reinstalling over live daemons** stop them: `squeezr fleet stop
  <storeKey>`. Otherwise the binary is locked and Restart Manager will close
  it for you.
- **Uninstall does not touch the `~/.squeezr` store** — workspaces, models
  and the compose file survive a uninstall; removing them is manual, after
  `squeezr drop` and `squeezr services down`.
- **The embedding model is not in the package** (+131 MB for optional
  semantics) — fetched separately via `scripts\fetch-model.ps1`.

Moved from an old Windows machine? The migration checklist —
[docs/migration-windows-to-linux.md](docs/migration-windows-to-linux.md).
Project still on the old layout with `.squeezr/` in the root? Migration to the
central store happens by itself —
[docs/migration-central-store.md](docs/migration-central-store.md).

## TUI: the human interface

Don't feel like remembering flags — run `squeezr tui` (or bare `squeezr` in an
interactive terminal: with no arguments it opens the TUI). If the terminal is
not interactive (pipe, CI) — the binary honestly falls to the help instead of
a menu.

The menu is contextual — it depends on workspace state:

- **Folder without a workspace** (no workspace found — squeezr searches up the
  tree from the current folder) — an immediate "No workspace found here.
  Initialize now?" (default — yes) → the `init` form. Declining is not a dead
  end: a two-item menu remains — Initialize workspace / Exit, change your mind
  without a restart.
- **Workspace initialized** — the full circle: Dashboard / Search / Map /
  Index / Add repository / Config / Drop workspace / Token stats / Usage
  analytics / Exit.

Top-level items:

- **Dashboard** — the summary: file/symbol/chunk counters per repo, both
  services' state (qdrant — points/pending, ollama — running + the provider's
  model installed or not), pending embeddings, index age. A service down — an
  explicit hint under the status: `qdrant is down — start it with: squeezr
  services up` (same for ollama). No repositories yet — a trailing "No
  repositories yet. Add one now?" (default — yes) → the Add repository form.
- **Search** — type a query, get hits with `[exact]`/`[semantic]`/`[both]`
  flags (repo, file:line, snippet — no bodies). Picking a hit expands the span
  with context from the live file; "More context (×2)" grows the context while
  the budget holds. Qdrant down — a warning and exact-only results.
- **Map** — the map browser: repo → directories → file → symbols with
  signatures (no bodies). From a symbol — the same span expand.
- **Index** — incremental, full reindex or `--reembed`, live progress by
  phase.
- **Add repository** — the `add` form instead of flags.
- **Config** — forms over the workspace config (in the store:
  `~/.squeezr/workspaces/<storeKey>/config.json`): qdrant, embeddings
  (ollama/openai), chunking, indexing. Input validation (URL, int > 0,
  provider), a "was → will be" diff summary before saving, and after an
  embeddings change — a reminder about `index --reembed`.
- **Drop workspace** — confirmation and full removal of the workspace index
  (`drop`).
- **Token stats** — per-workspace telemetry analytics: the per-tool table,
  savings, cache hit-rate, By agent / By session cuts — the same content as
  `squeezr stats` (one shared renderer). A snapshot taken on entering the
  screen; fresh data — re-enter.
- **Usage analytics** — a global rollup over every workspace in the registry:
  per-workspace blocks with stale marks + a final By agent table — the same as
  `squeezr usage` without flags. The current workspace is not needed by
  definition (fleet-wide data); the item lives in the common menu.

Everything is also available through CLI flags — the TUI is a layer, not a
replacement.

## MCP server: the agent navigates the index, not the files

`squeezr serve` runs an MCP server (stdio, JSON-RPC on stdout). The agent gets
the index as six tools — map, search, symbols, references and span expansion —
and never reads whole files at all.

### Attach: `squeezr mcp install`

No need to edit configs by hand — one command registers the server with a
client:

```bash
squeezr mcp install                          # interactive: detect clients → multi-select → scope → instructions
squeezr mcp install --client claude --scope project
squeezr mcp install --client all --scope user --yes   # every found client, no questions (for scripts)
squeezr mcp install --client all --yes       # "fix everything" after an upgrade: rewrites registrations to the current path
```

| Flag | What it does |
|---|---|
| `--client claude\|kimi\|qwen\|codex\|all` | Whom to attach; without the flag squeezr finds installed clients itself and offers a multi-select; `all` — every found one |
| `--scope user\|project` | `user` — the server is available in all projects, `project` — only in this workspace; without the flag it asks (with `--yes` the default is `user`) |
| `--watch` | Register the entry as `serve --watch`: this client's sessions run the background indexer themselves. Off by default (070/T5) — a plain `serve` answers queries and refreshes what a query touches; the watcher is a fleet decision you start on purpose, and the store claim makes an extra `serve --watch` an observer, not a second indexer |
| `--no-watch` | Kept for compatibility: it became the default, so an old pasted command still means exactly what it says |
| `--no-instructions` | Do not write the agent the navigation-instructions block |
| `--yes` | Non-interactive mode: zero questions (for scripts); requires an explicit `--client`; the instructions block is NOT written in this mode — only with `--instructions` |
| `--instructions` | Explicit consent to the instructions block, paired with `--yes` (without `--yes` the flag does nothing; if set together with `--no-instructions` — the latter wins) |
| `--json` | Machine-readable answer instead of the table |

What happens under the hood: the entry is registered as `serve` — since 070/T5
registrations no longer breed a watcher per client session (`--watch` opts a
client back in) — the
config is written by the client's own CLI (`claude mcp add`, `qwen mcp add`,
`codex mcp add`), and where there is no CLI (Kimi Code) squeezr merges the
JSON into the config file itself. `command` gets the **stable path** —
`~/.local/bin/squeezr` if that symlink exists (a dev build from the repo — the
binary's own path): after `install-release.sh` clients pick up a new version
on their next reconnect, stale registrations are gone. Foreign servers in the
config are untouched; a repeated install updates the entry on the direct-write
path (Kimi), while via the CLI path the client answers "already exists" —
that is fine, the entry is already in place. It separately asks (default —
"yes") whether to append the agent the marker block with the navigation flow:
idempotent, into the client's instruction file (Claude — CLAUDE.md, Kimi and
Codex — AGENTS.md, Qwen — QWEN.md); `mcp remove` takes it back out.

`squeezr mcp doctor` — the attach checkup: client found, entry present
(user/project), binary on PATH, workspace indexed, headless status through the
client's CLI. It also reads the managed hook block that `mcp install` arms in
the same pass and `mcp remove` strips: one `hook pre-read: <state>` line per
client (`armed`, `armed (project scope)`, `not armed`, or the reason the layout
cannot be armed at all — hook layout unverified), plus an additive `hookStatus`
per entry in `--json`; the arming rules are under **Advisory hook**. Plus
detection of **stale registrations**: if a client config
names a path other than the stable one, doctor shows `stale` with the pair
"registered → expected" and a ready repair command (`squeezr mcp install
--client <id> --yes`); in `--json` — additive
`userCommand`/`projectCommand`/`userStale`/`projectStale`/`expectedCommand`
fields, the exit code is unchanged by staleness. Doctor also reads the store's
watch claim: one `watch` row names who runs the background indexer (pid and
kind), how many live processes merely observe, and — when nobody owns the loop
— the exact command that starts one (`squeezr fleet restart <storeKey>`); in
`--json` an additive per-entry `watch` object (`name`, `storeKey`,
`ownerState` = alive/wedged/gone/foreign/none, `ownerPid`, `ownerKind`,
`ownerHost`, `observers`, `startCommand`). This is the checkable half of the
registration default flip: re-registering clients without `--watch` trades a
watcher per session for a deliberately started one, and doctor says whether
the store still has an indexer at all. `squeezr mcp remove [--client
...] [--scope ...]` removes the entry and the instructions block; both
commands honor `--client` and `--json`. A live `serve` compares its own binary
against the stable symlink target at startup and prints one stderr hint on a
mismatch: "the registration may be stale — run doctor/install" (silent when
the symlink is absent or everything matches).

### Project-level onboarding

The default instructions block is per-client and per-machine — it never lands
in the repo. The project block is the other tier: a committed
`squeezr:project-instructions` section in the project's own agent files, so
everyone who clones the project gets the same navigation rules and command
cheat-sheet.

```bash
squeezr mcp install --project   # write/refresh the block; no client registration
squeezr mcp remove --project    # strip the block from every carrier file
```

Without `--client`/`--all` the command only writes the block (block-only mode,
works non-interactively); combined with clients it registers them and writes
the block in one pass. Targeting: `AGENTS.md` always (created when missing);
`CLAUDE.md`/`QWEN.md` only when they are real files — a one-line `@AGENTS.md`
import pointer is left untouched. Plain `mcp install` never touches project
files: the flag is explicit (`--yes` does not imply it), and the interactive
prompt for it defaults to "no".

Re-running install refreshes the block in place between its markers — no
duplicates. The block header carries the squeezr version that wrote it: after
upgrading squeezr, re-run `squeezr mcp install --project` to refresh the text.
If a target file has one-sided markers (a start without an end or vice versa),
the file is left untouched and you get exactly one stderr warning naming it.
`mcp remove --project` strips the block from every file carrying it and never
deletes files — a file left empty is fine.

### Manual configuration (reference)

If you want to write the config yourself — where the `squeezr` entry goes:

| Client | user-scope | project-scope |
|---|---|---|
| Claude Code | `~/.claude.json` | `.mcp.json` |
| Kimi Code | `~/.kimi-code/mcp.json` (or `$KIMI_CODE_HOME/mcp.json`) | `.kimi-code/mcp.json` |
| Qwen Code | `~/.qwen/settings.json` | `.qwen/settings.json` |
| Codex CLI | `~/.codex/config.toml` | `.codex/config.toml` (trusted projects only) |

JSON clients (Claude, Kimi, Qwen) — the `mcpServers` block:

```json
{
  "mcpServers": {
    "squeezr": {
      "command": "squeezr",
      "args": ["serve"]
    }
  }
}
```

`["serve"]` is what `squeezr mcp install` registers by default: the session
runs no watch loop, it only queries the index. Add `--watch` when this
session should own the indexer — it then becomes the process the watch claim
names, and `squeezr fleet stop` is aimed at it. Whether a store has an owner
at all, `squeezr mcp doctor` answers in its `watch` row: the owner (pid,
kind, host) or the exact `squeezr fleet restart <storeKey>` command to give
the store one.

Codex — a `[mcp_servers.squeezr]` TOML section with the same
`command`/`args` (plus `startup_timeout_sec = 30`, sized for the freshness
pass the first cold query runs, not for an indexer boot). No `cwd` key is
needed in the entry — the workspace root is resolved
by `serve` itself from the process cwd (and `CLAUDE_PROJECT_DIR`, see above).

### Advisory hook: `squeezr hook pre-read`

A PreToolUse hook on the read tool, for every runtime whose hook layout squeezr
has verified. The command reads the hook payload from stdin and, if the file
lives in a registered workspace and looks like a bulk read — ≥ 256 KB on disk,
or a wide range requested (limit ≥ 400 lines) — it answers with a soft nudge,
verbatim: "This workspace is indexed by squeezr. For a read this size,
code_search or symbol_lookup can locate the relevant lines, and
expand_span(file, startLine, endLine) returns just that span from the live
file. If the file or repo is not in the index, a plain Read is correct." An
explicitly narrow limit suppresses the nudge: the agent is already reading a
targeted span. The hook only ever sees calls the host's matcher routes to it,
and the advice lands only when the payload parses and the rule above says
"bulk". A payload that quotes its numbers — the shape a paged Qwen read takes
when the host forwards the model's raw arguments (`"offset":"437"`) — parses
exactly like the numeric one: quoted decimal integers are read as numbers
(leading zeros and a sign parse too), and a quoted non-digit fails the whole
payload open — no advice and no telemetry row, on `offset` as well as `limit`.
Measured delivery: the whole-file read in
an armed live Qwen session (`audits/hook-live-2026-11-11/`); the quoted-number
arms by in-process A/B replay against the armed command
(`audits/t6-028-fix-2026-11-12/`).

**`tool_name` carries the runtime's own id.** Qwen sends `read_file`, Claude
sends `Read`. Nothing is normalized: the command accepts a declared set —
`Read`, `read_file`, `ReadFile` — compared Ordinal-exact, so a name nobody
measured stays silent instead of being lowercased into something invented.
`Read`/`ReadFile` are accepted *matcher aliases* on Qwen (its hook surface lets
a pattern name the read tool either way), while the payload Qwen actually
delivers still says `read_file` — which is why the armed Qwen pattern is
`^read_file$` and not an alias. The telemetry row keeps whatever the payload
carried, verbatim, because that verbatim value is the runtime evidence `usage`
reads back.

**The advice is a JSON object, never bare text:**

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"…"}}
```

The shape is load-bearing: the Qwen aggregator collects `additionalContext`
without requiring `permissionDecision`, so the text arrives in the model's
context. Plain text on stdout at exit 0 would reach the human as a UI
`systemMessage` and never touch the model — a nudge nobody reads.

Everything else — silence: a non-workspace path, a small file, a missing or
guarded file at a narrow or absent limit, malformed stdin → empty stdout,
exit 0. (A wide requested limit of ≥ 400 lines answers before the guard and
before the disk is consulted — that over-answer is the open defect BUGS/027,
not documented behavior.) The hook is advisory-only — it
sets no `permissionDecision` and never blocks a read; internal errors are
fail-open (silent, code 0). No network — only the workspace registry and a
stat of the file, so it is cheap to run on every read.

**Arming is `squeezr mcp install`'s job now.** `<removed 2026-11-11 — 114/T3>`
~~The snippet is applied by hand (crew policy: agents are forbidden from
writing hook configs).~~</removed> One command writes the managed block,
`mcp doctor` reports the state it measured, `mcp remove` takes it back out:

| Client | armed matcher | hooks file (user scope) | doctor state |
|---|---|---|---|
| Claude Code | `Read` | `~/.claude/settings.json` — a different file from the `~/.claude.json` server entry | `armed` |
| Qwen Code | `^read_file$` (anchored: Qwen evaluates matchers as regex) | `~/.qwen/settings.json` — `hooks` lands as an additive sibling of `mcpServers` | `armed` |
| Kimi Code, Codex CLI | — | not armed (hook layout unverified) | `not armed (hook layout unverified)` |

The written entry takes this shape — the command is the canonical absolute path
of the binary, quoted when it contains a space:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "^read_file$",
        "hooks": [
          {
            "type": "command",
            "command": "/home/you/.local/bin/squeezr hook pre-read"
          }
        ]
      }
    ]
  }
}
```

`--scope project` arms the workspace's own settings file instead
(`<root>/.claude/settings.json`, `<root>/.qwen/settings.json`); `mcp install
--project` alone — block-only mode, no clients — arms nothing, and no flag
skips the arm: it rides with the client registration. There is no
hook-specific opt-out.

What counts as managed is defined strictly: the command equals the one install
computes, or it is `<exe> hook pre-read` with the executable named `squeezr`.
So a snippet applied by hand before this feature is managed too — install
normalizes it into the canonical form, remove strips it. Merge-safety, stated
at the altitude it was proven: foreign entries under `PreToolUse` are preserved
by canonical serialization, and whole-file byte-identity is proven against the
canonical pre-image — the JSON round-trip reformats the file (the same posture
as the direct config writer), a foreign shape the merge cannot resolve refuses
the write before touching it, and a second install writes neither the block nor
a `.bak`. The `.bak` a re-install does leave behind comes from the
server-entry write; the hook upsert stays silent because the block is already
canonical.

The `hook pre-read armed` line and the additive `hooksArmed` key claim a state,
not a write: they are true for any client whose hook layout resolves, so an
idempotent re-run still says armed having moved no byte. `mcp doctor` is the
place that answers what is actually on disk.

**Why the hook exists, in the numbers.** Every matching call into a registered
workspace appends a telemetry row (`client: "hook"`), and `squeezr usage --json`
rolls them up under `totals.sessions`: `hookSessions` is the distinct set of
reading sessions and stays the denominator the adoption ratio divides by, while
`hookSessionsByTool` holds one bucket per verbatim recorded tool name and
`hookSessionsWithoutTool` counts the distinct sessions whose rows named no tool
at all. Read the buckets as *observed under this name*: a session that read
through two tools is counted in both, so buckets overlap and are not exhaustive
— **never sum them to get `hookSessions`**, and `hookSessionsWithoutTool` sits
outside the per-tool list. The runtime name beside a tool is a reading rule,
not a matcher: a `Read` row is called Claude Code because that is what the row
says, not because some other host was ruled out.

### Tools

| Tool | Parameters (defaults) | Returns |
|---|---|---|
| `repo_map` | `repo?`, `prefix?`, `depth?` (2), `maxTokens?` (4000) | the repo tree: directories → files → members with signatures. No bodies, a map for navigation only. `prefix` (e.g. `"src/"`) narrows the output to a subtree — directory names render relative to the prefix, `depth` keeps its structural meaning (1 = directories, 2 = files, 3 = members) |
| `code_search` | `query` (required), `repo?`, `prefix?`, `topK?`, `maxTokens?` (2000), `kind?` (`all`/`code`/`docs`, default `all`) | hybrid matches `{repo, file, line, score, source, snippet}`; source = `exact`/`semantic`/`both`; snippets are capped previews — the matched-ish line plus a bounded window, ≤ ~200 chars, so one hit can no longer spend the whole `maxTokens` (`expand_span` reveals the body); bodies are never returned. One hit = one file: the best span wins, the file's other spans collapse into `alsoMatched` (the field appears only when ≥1), so `topK` bounds distinct files, not spans. `prefix` filters both arms (exact and semantic) by a directory subtree. Ranking honors the `indexing.rankExclude` config: excluded trees never surface but stay indexed. The hybrid branch gained an identifier-exact arm: multi-token queries with code-shaped tokens (camelCase, `snake_case`, length ≥ 12) or rare tokens (df ≤ 5% of docs) take a separate branch with `source=exact` — more precise than phrase-literal for compound names. The `repo`/`language`/`pathGlob`/`prefix`/rankExclude filters narrow that branch too — through the same `BuildFilter` as the phrase arm. Branch limitation: trigram case-expansion is capped (`CaseVariantCap = 64` divided across the token's trigrams, 8 variants per trigram), so a lowercase token longer than ~10 chars cannot reach a spelling that exists only in PascalCase and silently drops out of the discriminants — a quiet recall loss (the phrase arm in the same case goes to a FullScan over disk; single literals never lose). The cure is case-folded trigrams in the index: an index-format change with a rebuild, no backlog feature for it yet. `kind` filters what a hit can be: `all` (default) matches everything, `code` keeps program sources, `docs` keeps prose and config (markdown/text/json/yaml/toml/xml); it applies to both hybrid arms — Qdrant payload filter on the indexed `kind` field, meta.db file-kind join on the exact side — and unknown values are rejected with the allowed list instead of silently meaning `all`. Points without a `kind` payload (orphans, or points written by pre-057 binaries until the backfill converges them) match only `kind=all` |
| `symbol_lookup` | `name` (required), `kind?`, `repo?`, `maxTokens?` (300) | the symbol's definition: signature, doc comment, `span`, body — if it fits the budget. When a type's body does not fit → `members` (name, kind, line) in the same budget + a `membersOmitted` counter: every line is a ready coordinate for `expand_span`, no more dead ends. A body that was refused **and replaced by the full member map** is the designed outcome, not a cut answer: `truncated: false`, `cost.complete: true`, no `truncated:` warning, and the hit says `bodyOmitted: true` — read the state through that flag, never through the absence of a `body` key (the JSON writer drops nulls, so a mapped hit has no `body` field at all). `truncated: true` means the answer really lost something (a dropped hit, `membersOmitted > 0`, a dropped `did you mean`, or a body no map can stand in for), and the warning then names which loss it was — the no-substitute case prices the body instead of offering to narrow a query that already lost its only hit: `… — raise maxTokens (the body alone needs ~N tokens)`. When `truncated: true` because not one member row fit, there is no `members` key and `membersOmitted: N` names the rows that never fit — a counter without a map means N refused and none shown, never a silently complete map (a symbol with genuinely no members carries neither field). Symbol not found → empty `data` + `didYouMean` (top-3 candidates by trigram-Jaccard, threshold 0.34), not an error |
| `find_references` | `symbol` (required), `repo?` | every call site `{repo, file, line, kind, enclosingSymbol}` — `repo` on each row distinguishes byte-identical paths from different repositories (a checkout plus its worktree). Same-name definitions sharing one call list yield one row set, and the envelope carries additive `definitions` — `{repo, kind, file, startLine, endLine}` per declaration behind the collapsed rows (the same fields as `refs --json`). The field appears in exactly two cases: two or more same-name declarations share one call list, or the symbol has no calls at all (otherwise an empty `data` would say nothing about declarations); in other answers the field is absent and the envelope stays six-keyed. `definitions` is metadata of the collapsed rows: it does not count into `tokensUsed`, same as the CLI. enclosingSymbol is captured by Roslyn at index time (exact, partial classes included); for indexes built before capture the value is inferred from symbol spans (a fallback inference). Symbol not found → tool error (like `squeezr refs`, exit 1) |
| `expand_span` | `file`, `startLine` (required), `endLine?`, `contextLines?` (3), `maxTokens?` (1500) | the span's lines + context from the live file on disk (sees edits made after indexing). The only tool that returns source lines |
| `code_pack` | `name` (required), `repo?`, `kind?`, `maxTokens?` (2000) | the task-shaped front door: one call answers "what is this symbol and who touches it" — definition (signature, doc comment, `span`), the defining member's body (`bodyLines`, or the `members` mode for oversized types), every caller as `{repo, file, line, kind, enclosingSymbol}`, trust signals (`guardWarnings`, `laggingDeclarations`, `additionalMatches`), and the per-file `cost` block. Budget order: definition and body spend first, callers truncate — `withheldCallers` counts what did not fit, `complete:false` names it. `complete` follows the answer state, not the budget's mood: `complete = withheldCallers == 0 && the lookup was not clipped`, so a pack whose oversized type body was replaced by the full member map answers `complete: true` with `lookupTruncated: false` and `bodyOmitted: true` (the pack's own flag, read from the one hit it rendered — with several matches it describes the rendered pack, not every candidate), while `withheldCallers > 0` still answers `complete: false` with its own warning line. Measured live on a 2.6k-file store: ~2.3× faster than the equivalent `symbol_lookup` → `expand_span` → `find_references` chain (one store session instead of three; the ratio narrows on bigger stores — the saved overhead is constant). Prefer the per-tool calls when you need only one of the three answers — the pack buys breadth, not depth |
| `index_status` | — | freshness: index age, per-repo file/symbol/chunk counters, the `kindChunks` block (chunk counts per kind: `code`/`docs`; the values sum to the per-repo chunks counts), the `ignored: { files, bySource }` block per repo (excluded files broken down by source: defaults/config/squeezrignore/gitignore; zero-count sources absent), pending embeddings and `embeddings.coverage` (the embedded share of chunks, 0..1; `null` when there is nothing to embed), Qdrant availability, the workspace `rootPath` (if it is not your project's root, this session is looking into a foreign workspace). When exclusions exist, `warnings` name the dominant source and how to turn it off. When ranking is curated, `warnings` list the active `indexing.rankExclude` patterns. **Additive (035 contract):** if the registry holds two or more workspaces, `data` carries `workspaces` — an array of `{ name, rootPath, storeKey, current, stale }` (at most 12 rows, the current session's row first; the rest counted in `workspacesOmitted`). With one workspace neither key exists, and the `data` output matches the old one byte for byte — existing fields are neither renamed nor removed |

All tools have a default token budget (see the table). `truncated: true` means
the answer lost something it was asked for, and the warning then names the loss
and the way out: narrow the query (`repo`, `kind`) when a hit was dropped,
`expand_span` a member when map rows were cut, or raise `maxTokens` when the
body price the line prints is worth paying. <removed 2026-11-10 — 115/T6>~~on
`truncated: true` narrow the query (`repo`, `kind`) or raise `maxTokens`
explicitly~~</removed> — one remedy for every refusal is what lied: it offered
to narrow a query that had already lost its only hit, and it fired on answers
that lost nothing at all. See **Budget states** under the envelope semantics.

### Freshness

Tools that name files answer with a per-file verdict, not a gut feeling: the
additive `fileFreshness` block carries `{repo, path, verdict, lagSeconds?}` —
`fresh` (the index knows the file's current state), `lagging` (the disk has
moved past the index; the lag is named where the clock shows it), `unknown`
(the read path never classified it). The projection is a stat against the
indexed stamp (~6 ms per answer on the largest store), while the freshness
guard stays the content-exact authority — a file it refreshed inline reads
`fresh` afterwards, and one whose inline refresh failed stays `lagging` even
when its mtime reads young. A live watcher on the store is named in the same
answer (`active writer on this store: pid N, watch beat Ns old`) so a verdict
about to flip is never mistaken for a settled one. `index_status` aggregates
rather than names files and says so in `warnings`; `repo_map` names a bounded
sample of up to 50 rows of its own scope in `staleFiles` and declares what it
left out in `freshness` — the vanished rows are listed first (`listedMissing`
of them), the lagging rows after.

### Navigation flow

A two-tier output: tools give coordinates, code opens only through
`expand_span`.

1. `repo_map` — an unfamiliar repo: structure and signatures, zero bodies.
2. `code_search` (by text) or `symbol_lookup` (by name) — points of interest:
   file:line, snippets, signatures.
3. `find_references` — who calls the found symbol.
4. `expand_span` — open the span with context when you need the code itself.

### Response envelope semantics

Every tool answers in one envelope:

```json
{ "data": ..., "tokensUsed": 42, "truncated": false, "warnings": [], "indexAge": "00:12:03", "staleFiles": [] }
```

- `tokensUsed` / `truncated` — the actual cost of the answer. `truncated`
  means "this answer lost something the caller asked for", not "the budget
  refused at least once"; `cost.complete` means "the answer holds everything it
  claims" — true for a whole answer and for a mapped one, false only when
  something really went missing, and an answer that carries less of its own
  freshness verdict than its check found is not holding everything it claims.
  The freshness story an answer ships beside its content is priced separately,
  in `freshness.freshnessTokens`.
  <removed 2026-11-10 — 115/T6>~~`truncated: true` → narrow the query (`repo`,
  `kind`) or raise `maxTokens`~~</removed> — that rule conflated a substituted
  body with a lost one. Read the three states below.
- `warnings` — soft degradation (e.g. "vector store unavailable, falling back
  to exact search only" on a full outage, or "semantic coverage: 76% (3017
  chunks pending embedding)" in `code_search` when the semantic layer is not
  fully pumped — at 100% the line is absent), trimming hints, and "cache:
  invalidated (index changed)" — the cache-death mark (see below).
- `indexAge` / `staleFiles` — freshness. `staleFiles` names indexed rows the
  answer can no longer vouch for, in one of two classes: on `repo_map` it is
  the verdict of a structure pass (a row whose file is gone, or whose size or
  mtime moved — no content is read there and nothing is refreshed), on
  `code_search` / `symbol_lookup` / `find_references` it is the content guard
  (modified files refresh inline on read, inside a per-call budget — a file
  count and a wall bound, both named by the warning line that appears when a
  re-read was refused; `RefreshBudget` is the one place they are declared).
  Whatever could not be refreshed — refused by that budget, or attempted and
  failed — lands honestly in `staleFiles`, and `unrefreshedFiles` says how
  many of them the budget refused. Either way the agent sees the data behind
  those files is not confirmed and may suggest `squeezr index`. The final call
  on a reindex belongs to the workspace owner.
- `freshness` — the attestation of a check, carried by the four tools that run
  one (`repo_map`, `code_search`, `symbol_lookup`, `find_references`) on every
  answer they produce, and additive over the core keys above:
  `{check, checked, scopeRows | namedFiles, staleTotal, listed,
  freshnessTokens}`, plus `listedMissing`, `missing` and `changed` on the map's
  structure pass and `laggingFiles` and `unrefreshedFiles` on the content
  tools. A stored answer that
  the cache hands back keeps the block it was produced with and marks itself
  (`replayed`, third sub-bullet): the block attests a check, and on a replay the
  check ran in the earlier call. It says which class of check ran
  (`structure` or `content`), how many positions it examined against the volume
  the answer speaks for (`scopeRows` — the rows of a map scope, withheld and
  unresolvable ones included; `namedFiles` — the distinct files a query answer
  names), how many came back stale, and how many of those the answer actually
  lists. An empty `staleFiles` with `checked: 0` means nothing was examined, not
  that everything is fine; a `listed` below `staleTotal` means the answer
  carries a sample, and `cost.complete` says so.
  - `freshnessTokens` is what this story costs the caller and `tokensUsed`
    does not count: the `staleFiles` sample, the `warnings` lines that explain
    the check, and this block itself, measured on the wire basis (a quote
    travelling inside the MCP content string costs six bytes, and the product's
    own estimator prices those bytes). Folding them into `tokensUsed` was
    rejected — that number is what the caller's budget governs — so telemetry
    records `tokensUsed + freshnessTokens`, the number actually paid.
  - `laggingFiles` counts the files this answer calls `lagging` in
    `fileFreshness`. Those are stat verdicts (size or mtime against the stamp,
    never a content read), so a `staleTotal` of `0` says nothing about them and
    the answer no longer reads as a clean verdict while naming them.
  - `unrefreshedFiles` counts the stale files this call examined and then
    refused to re-read because the inline-refresh budget was spent. The budget
    is two numbers declared once, in `RefreshBudget` — a count of re-reads a
    call may start and a wall bound read between candidates — and the
    `warnings` line that appears with it says both. Absent means the budget
    never bound: either nothing was stale, or the call never reached a
    candidate (a store it could not open collapses every candidate and
    attributes no cause, and that shape is never charged to the budget). A
    refused row costs the answer nothing extra: it is still named in
    `staleFiles`, still counted in `staleTotal`, still `lagging` in
    `fileFreshness`. What the budget buys is the caller's wall time — a
    re-measured `code_search` over a repo naming 24 stale files spent 46.7 s of
    inline re-reads before the budget existed and 5.9 s after it (at 12 stale
    files, 27.9 s → 6.0 s; medians of ten cold `squeezr serve` runs per arm,
    harness `scripts/bench-guard-017.sh`), and the 23 rows it left unread are
    named in the answer instead of being silent. `tokensUsed` did not move
    between those arms — 815 before, 815 after — while `freshnessTokens` rose
    from 127 to 601, which is exactly what disclosure costs.
  - `replayed: true` appears **only** on an answer the response cache handed
    back (see below). File mtimes move no cache generation, so on a replay the
    numbers in this block — and `staleFiles` and `indexAge` beside them —
    describe the call that produced the stored envelope, not this one. A
    replayed answer attests no check of its own; the mark is how the reader
    knows.

**Budget states: complete, mapped, clipped.** `symbol_lookup` and `code_pack`
spend their answers out of one `TokenBudget`, and since 115 the engine keeps
the distinction instead of flattening it. Three states, the worst severity in
the answer wins:

| state | what happened | what the wire says |
|---|---|---|
| `complete` | nothing refused | `truncated: false`, `cost.complete: true`, no `truncated:` warning |
| `mapped` | the type's body did not fit and the **full** member map stood in for it (no row omitted) — the designed outcome the tool description promises | `truncated: false`, `cost.complete: true`, no `truncated:` warning, and the hit carries `bodyOmitted: true` with no `body` key at all |
| `clipped` | a hit was dropped, map rows were omitted (`membersOmitted > 0`), a `did you mean` was refused, or a body was refused with nothing to substitute for it | `truncated: true`, `cost.complete: false`, and one `truncated:` warning naming which loss it was |

Read the state through `truncated` plus the hit's `bodyOmitted` / `members` /
`membersOmitted`. `bodyOmitted: true` on its own is not `mapped` — a clipped
no-substitute answer sets it too — and the flag, not the missing `body` key, is
the discriminator (the JSON writer drops nulls, so "no body" and "no body
field" are the same bytes). Surfaces with no mapped state (`code_search`,
`repo_map`, `expand_span`, `find_references`, `index_status`) pass `!truncated`
explicitly: a refusal there really does drop rows, so their pair stays as
shipped.

The warning keeps its declared shape `truncated: {used} of {ceiling} token
ceiling — {remedy}` (plus the legs clause where a surface has one), and the
remedy names what was refused. The symbol family — `symbol_lookup`, and the
lookup leg of `code_pack` — prints these three over MCP:

- a hit dropped → `narrow the query (repo, kind) or raise maxTokens`
- map rows omitted → `read a member with expand_span or raise maxTokens`
- body refused with nothing to substitute → `raise maxTokens (the body alone needs ~N tokens)`

The CLI says the same three in flag vocabulary and puts them in `hint` rather
than a warning line:

- `narrow the query (--repo, --kind) or raise --max-tokens`
- `read a member with expand <file> <line> or raise --max-tokens`
- `raise --max-tokens (the body alone needs ~N tokens)`

Two more texts exist outside that family, and this list is the symbol family's,
not the whole wire: `expand_span` carries its own legs clause,
`truncated: 1494 of 1500 token ceiling — raise maxTokens, or narrow the span (startLine/endLine)`
(live, `audits/truncation-state-2026-11-10/raw/remedy-catalog-round4.jsonl`), and a
zero-hit answer whose `did you mean` text itself did not fit has no hint to render,
so both roads fall back to the bare `truncated: 0 of 1 token ceiling — raise maxTokens`
(same carrier, calls #43/#44 — the suggestions are dropped and the answer is clipped).

A mapped answer emits no truncation warning at all — the body price is the one
case where the caller can only weigh the gap against a number. The price string
has one home (`HintText.BodyPriceRemedy`); the two older texts still come from
the engine's `TruncationHint`. One home for the *choice* between them is a
target, not a fact: the no-substitute predicate is written at three sites today
(`HintText.RemedyForCli`, `SymbolLookupTool.RemedyFor`,
`CodePackTool.RemedyFor` — the pack's variant reads the pack, so it cannot
collapse onto the result-typed CLI helper), and folding them into a single
predicate-shaped helper is the named follow-up.

`raise maxTokens` re-runs inside the session you are in: the resolved ceiling is
part of `symbol_lookup`'s response-cache key, so the same name at a bigger
ceiling gets its own envelope instead of the stored one (`BUGS/024`, closed by
115/T7 — its additive `params.maxTokens` row key is declared in the CHANGELOG).
An identical ceiling still replays, which is the point of the cache; `code_pack`
keyed its budget this way before `symbol_lookup` did.

The CLI json is not the envelope. `squeezr symbol --json` says the same thing
in its own vocabulary: additive `state` (`complete`/`mapped`/`clipped`) as the
last key, `truncated` narrowed with it, and `hint` emitted only on a clipped
answer in `--flag` spelling (`raise --max-tokens (the body alone needs ~N
tokens)`, `read a member with expand <file> <line> …`) — a script branching on
the old `narrow the query (--repo, --kind) …` text for a no-substitute answer
breaks, because that case now prints its price instead. `state` is CLI-only by
design: spec requirement 4 forbids a new envelope key, while `cost` and
`warnings` are envelope-only. `squeezr map --json` carries no `state` at all —
`repo_map` has no mapped answer, a map that runs out of budget has lost rows.
The human budget line follows the same split: `tokens: N` on a complete or
mapped answer, `tokens: N, truncated — <remedy>` on a clipped one. One asymmetry
to keep in mind while reading `complete`: `symbol_lookup` over MCP always asks
for the body, while CLI `symbol` asks only with `--body` (or when exactly one
symbol matches), so a multi-match `squeezr symbol` answer is `complete` with no
bodies at all — `complete` means nothing was refused, not that a body came back.

Repeat queries are nearly free: the cached tools (`code_search`,
`symbol_lookup`, `repo_map`, `find_references`, `code_pack`) cache answers
within a serve session — an LRU of 100 entries, keyed by the canonical JSON of
the parameters (property order and null-vs-absent do not affect the key).
`symbol_lookup` and `code_pack` put the **resolved** ceiling into that key, so a
raised `maxTokens` is a different entry and a different answer; `code_search`
and `repo_map` key the value as it was passed, so leaving `maxTokens` out and
passing the default are two different entries there. An
identical query in the same session = an instant replay of the stored
envelope; telemetry marks it `cacheHit: true` (and it shows in the hit-rate
of `squeezr stats`). A replayed answer says so in its own block —
`freshness.replayed: true`, present only on hits and costing 15 text bytes (26
on the wire, which `freshnessTokens` re-prices) — because the generation is
the mtime pair of `meta.db` and `config.json`: files that changed on disk, or
vanished from it, never invalidate a stored answer, so the numbers a replay
carries describe the call that produced it. Any reindex — a watch pass or an external `squeezr index`
— changes the cache generation (mtime of meta.db): dead entries are dropped
immediately, not left for LRU eviction, and the first answer after the change
carries `cache: invalidated (index changed)` in `warnings` — once per
generation per session, never baked into the cached envelope and never
multiplied on repeats. Such a query is marked `cacheInvalidated: true` in
telemetry, so "the cache died" is distinguishable from "there was no cache".
`expand_span` is not cached (it reads the live file from disk), `index_status`
is cheap on its own.

### Error plane: structuredContent

Success and error travel in different planes — a client must be able to read
both. Success — `content[0].text` with the JSON envelope above. Error —
`isError: true`, human-readable text in `content[0].text` and a
machine-readable contract in `structuredContent`:

```json
{
  "isError": true,
  "structuredContent": {
    "code": "workspace_not_initialized",
    "message": "Workspace is not initialized. Run 'squeezr init' first.",
    "rootPath": "/path/to/workspace",
    "nextSteps": ["cd '/path/to/workspace' && squeezr init", "cd '/path/to/workspace' && squeezr index"],
    "cause": "root_without_manifest"
  }
}
```

- `code` — the machine-readable cause (`workspace_not_initialized`,
  `index_not_built`, …), stable across releases within the additive 035
  contract.
- `nextSteps` — the exact commands for the next step, one dictionary with
  `squeezr mcp doctor` and `hook pre-read`.
- `cause` — a refinement inside the code where causes are several: for
  `workspace_not_initialized` it is `root_without_manifest` (the session is
  inside the root — initialize and index it) or `session_outside_root` (the
  session is outside the root — re-register the client, **do not seed a second
  store**).

In the CLI plane (`--json` on commands, not MCP tools) the error envelope
additionally carries `error.removedPaths` on drop operations: what already
got removed when the registry update failed.

`mcp doctor` (the `indexState` field in `--json`) and `hook pre-read` print
the same dictionary of codes and commands; doctor sees the session directory
and in the `session_outside_root` state (a session outside the anchored root)
returns rebind steps instead of the forbidden initialization, and
`workspaceIndexed` is derived from the same classification as `indexState` —
the flag and the state block never contradict each other.

## Watch mode: the index chases the working tree on its own

An on-demand index is a compromise: you edit a file and `code_search` still
answers about the old version. FreshnessGuard refreshes point-wise on read,
but the full catch-up pass you trigger by hand. Watch mode removes that too.

Two forms, one engine:

```bash
squeezr watch            # human: a daemon in the terminal, Ctrl+C — a clean exit
squeezr serve --watch    # agent: the same engine inside the MCP server
```

Registered clients run neither form by default (070/T5): `mcp install`
registers a plain `serve`, so the engine is a fleet decision started on
purpose, not a side effect of every client session; `squeezr mcp doctor` names
the owner of each store and hands the exact start command where nobody owns
one.

How it works: `FileSystemWatcher` on the repo roots → filtering (the same
ignore globs and extensions as indexing; store service files do not count —
they live outside the project) → changed paths accumulate in a changed-set →
after a quiet window, one incremental pass over everything accumulated. A
"save-all of 50 files" = one pass. The index is busy (a manual `index`, TUI) —
the engine waits with exponential backoff and never steals the lock; a second
`serve --watch` on the same store never reaches passes at all — the watch is
held by the claim owner, the second process becomes an observer and answers
queries without background passes. `squeezr watch` holds the same claim (one
`watch.owner` per store, the kind inside the marker only says who owns it):
a watch that is not the owner starts no engine, prints `warning: watch not
started: pid N owns this store; no watch loop runs in this process` on stderr
and exits 0 — a second indexer has nothing to do here. Live status: in a
terminal — the Live grid (workspace / status / last pass / error), in a pipe —
`pass started: N changed paths` / `pass completed: +A ~M -D` lines on stdout.
For programmatic consumption — `serve --watch --events`: NDJSON pass events
(`started` with `changedPaths` → `fileIndexed` → `completed`, `completed`
carries an `embedded` counter) on stderr, JSON-RPC on stdout untouched (the
schema is in the `--json` section above). Without `--events` serve is not
silent in three places: the deferred point-cleanup journal — a
`{"type":"watchPass","deferredReaps":N,"pendingReaps":M}` line, the embedding
drain — a `{"type":"watchDrain","at":"…","embedded":N}` line per pass that
drained something (or
`{"type":"watchDrain","at":"…","embedded":0,"idleReason":"…"}` once per idle
reason — an unavailable provider or vector store), and the watch role —
exactly one line at start, to both roles:
`{"type":"watchRole","role":"owner"|"observer","verdict":"<claim verdict>","ownerPid":N}`.
The observer adds a human line to that one:
`warning: watch not started: pid N owns this store; serving queries +
per-query freshness`. A pid is named only where the marker actually carries
one — `StoreUnavailable`, `MarkerUnreadable` and `RaceLost` without a marker
name their real cause and invent no pid. The same verdict reaches the agent:
the observer session's first answer carries it in the envelope's `warnings`,
further envelopes go clean. A failed graceful release of the marker is also
heard on stderr (`warning: serve watch: owner marker could not be released`,
repeated only if the text changed), a successful one is silent byte for byte.

The embedding drain: `serve --watch` carries the provider and the vector
store and re-embeds `embeddings.pending` down to zero itself — a manual
`squeezr index` for catch-up is not needed. The provider + store are
constructed once per serve process, in the background, before JSON-RPC starts
this is not observable (tools answer immediately even while the resolver
probes the endpoint). The drain rate is capped by the
`watch.embedBudgetPerPass` budget of chunks per pass; as soon as a pass has
spent its whole budget, the engine schedules the next empty pass itself — the
catch-up queue is spread over passes at debounce-window steps instead of
hammering the provider without pause. At drain start and on every pass, the
provider lines go to stderr (`watch embed drain ready (provider=ollama)`),
switch-offs (`watch embed drain off (…)` with a reason) and the `watchDrain`
counter — from these you compute the rate (chunks/min) and see the idle
cause. Embed failures degrade to warnings: chunks stay in
`embeddings.pending` (visible in `index_status`/`fleet`), serve lives on; a
store that died mid-pass simply eats the rest of the budget — under-embedded
chunks are picked up by the next pass. A provider or model switch (another
model/profile) is a modelKey change for chunks: the whole queue is
re-embedded once under the new key, the pass honestly warns `model key of repo
… changed: N chunk(s) carry vectors from another embedding model`; old keys
are not silently rewritten, points are overwritten by the same hash-ids. The
interactive `squeezr watch` carries no drain — it is the human observation
mode; FreshnessGuard in the query path also stays embed-free, so a read never
pays a model load.

Deletions — the full lifecycle (trigram tombstones, document/chunk cleanup,
Qdrant point drops for the file): after a `git checkout`/`git clean` the
answers contain no ghosts. `serve --watch` with the drain finishes the
`pending_reaps` journal in the nearest pass itself; a watch without a drain
(the interactive `squeezr watch`, `--no-embed-watch`) records the deletion
into the journal and the next `squeezr index` with embeddings finishes it —
not silently: `deferredReaps`/`pendingReaps` in the pass report. `serve
--watch` does not block JSON-RPC — tools answer during a background pass too,
the envelope unchanged. FreshnessGuard stays the safety net: until watch
catches up, the envelope honestly shows `staleFiles`.

Config — the `watch` section of the workspace config
(`~/.squeezr/workspaces/<storeKey>/config.json`):

```json
{
  "watch": {
    "enabled": false,
    "debounceSeconds": 2,
    "backoffMaxSeconds": 30,
    "embedPending": true,
    "embedBudgetPerPass": 256
  }
}
```

`enabled` turns the engine on for `serve` without the flag (the flag beats
the config); an explicit `squeezr watch` is always the user's intent and does
not consult the config. `embedPending: false` switches the embedding drain
off for `serve --watch` (passes remain, no embeds); `embedBudgetPerPass` —
the per-drain-pass chunk ceiling, zero and negatives are invalid and
deliberately fall back to the default (not "switch off differently": the only
ways to mute the drain are `embedPending: false` or `--no-embed-watch`). The
`--no-embed-watch` flag on `serve` mutes the drain regardless of the config.
A missing section or invalid values = defaults (engine off, drain on, budget
256).

## Fleet: all daemons at once

Watch daemons are scattered across workspaces, and remembering what runs
where is a machine's job. `squeezr fleet` shows every workspace in the
registry and the state of its daemons (works from any directory, no workspace
needed):

```bash
squeezr fleet         # table: daemons, pending, index freshness, stale
squeezr fleet --json  # NDJSON, one object per workspace (the 035 contract)
```

```
Name      Root path            Store key  WATCH                 SERVE-WATCH    Activity                       Pending  Last indexed      Status
squeezr   /home/dev/squeezr    squeezr    pid 4217, up 2h 14m   —              indexing docs · files 12/40    0        2026-09-16 10:31  ok
old-proj  /home/dev/old-proj   old-proj   dead (pid 9021)       —              idle?                          n/a      n/a               stale
```

The `WATCH` / `SERVE-WATCH` columns are the live processes of two kinds:
`squeezr watch` (terminal) and `serve --watch` (MCP client). Where a cell used
to hold a single pid, there are now as many as there are processes on the
store: every live pid on its own row, the claim owner marked `(owner)`, the
rest `(observer)`; a dead bit — `dead (pid N)` (the file remains but the
pid/bit went stale), no bits at all — `—`. The role mark appears only where
`watch.owner` actually names an owner: with no claim, fleet invents no
observer out of an absence of evidence. `Pending` — unprocessed chunks, `Last
indexed` — the last full pass time; both best-effort from the workspace's
`meta.db` (`n/a` if the DB is unreachable — fleet does not crash). Entries
with a vanished rootPath are marked `stale` and emit a stderr warning with a
`squeezr drop --store-key` hint — the verdict comes from the same
`OrphanScanner` as `workspace list` (067); fleet does not recompute it. Row
ordering (a store with a live daemon on top) is unchanged: a store counts as
live when at least one of its bits is alive.

The `Activity` column is what the live processes are doing right now, one
line per live pid in the roster's own order: `indexing <repo> · files 12/40`,
`embedding <repo> · chunks 3/9 · ollama · gpu`, `walking <repo>`, `semantic
<repo> · 5/20`. A live process between passes answers `idle`; a store whose
every beat aged out or whose pids are gone answers `idle?` — the question
mark is the honesty: an absence of evidence never renders as work. The
executor tail repeats what the daemon's own beat measured (`ollama · gpu`);
fleet names no hardware the beat did not name first. A store
with no roster at all answers `—` in this column.

The same roster reaches `fleet --json` as a per-store `daemons[]` array (`kind`, `pid`, `status`,
`uptimeSeconds`, `owner`, `host`, plus `isAlive` — the same derived member the sibling `watch`/`serveWatch`
objects already carry): every process the store's beats name, empty when the store names nobody, with exactly
the claim holder flagged `owner: true`. Each live row also carries what that process is doing and what it costs,
additive per 035: `phase` (`walk|files|semantic|embeddings|trigram`), `repo`, `doneItems`, `totalItems`,
`phaseSeconds`, `provider` and `executor` — the beat's own evidence, null between passes — plus `cpuPercent`
and `rssBytes`, measured by fleet itself over every live pid of every store in one shared 300 ms
sampling window per pass (null where a pid is gone or unreadable — never 0 standing in for an
unmeasured row). Machine values stay
machine: numbers and enum-like strings, human phrasing lives only in the table. A row that is not alive carries
none of it — a stale beat or a dead pid never publishes activity, and a daemon that has never started a pass
reads `phase: null` (`idle` in the table), not an empty cell. Otherwise additive per 035 — `watch`,
`serveWatch`, `stale`, `pendingChunks` and `lastIndexedAt` keep their names, their meanings and their order
among themselves; the array simply sits between `serveWatch` and `stale`.

How a daemon declares itself: every live watch-role process writes a per-pid
beat into the store dir — `~/.squeezr/workspaces/<storeKey>/beats/<kind>.<pid>.beat`,
kind = `watch` or `serve-watch`. Inside: JSON
`{pid, kind, startedAt, lastBeatAt, host}` — plus an additive `activity`
member while a pass runs (phase, repo, item counters, phase start, provider,
executor), absent again between passes; a beat every 5 s and after every
successful pass, the write is atomic (tmp + move), a clean exit removes the
file; the `beats/` directory itself is created by the writer and only inside
an existing store — a daemon whose store was carried away does not resurrect
the directory, it warns. A daemon is "alive" = `lastBeatAt` fresher than 30 s
AND the pid really exists — the same semantics as the old single-file
heartbeat. The role in the fleet roster is decided not by the beat and not by
a second marker but by `watch.owner`: the owner is exactly one per store,
regardless of kind. Besides the per-pid beats, the indexing process keeps the
former per-kind `<kind>.heartbeat` file — the shape binaries before 070 read
(the `index.lock` lock-owner report still does): only the live claim owner
stamps it, and a watcher demoted mid-run stops stamping the moment ownership
moves on and deletes the file while it still names its writer — so an old
reader sees the indexer or nothing, never whichever racer wrote last. The
demoted process keeps its own per-pid beat: the roster still counts it as a
live observer. Garbage is collected by the writer: a beat of a foreign process whose
pid is gone and whose beat went stale past the stale window is removed on the
next beat (at most once a minute) — otherwise fleet would name a daemon that
does not exist until the end of time; a stale beat of a live pid (a wedged
daemon) and a beat with a foreign host are left alone, and the per-kind
heartbeat remains as the evidence "the daemon died ugly" — it is the `dead
(pid N)` row.

Control — by exact storeKey (like `drop --store-key`, no fuzzy resolution):

```bash
squeezr fleet stop <storeKey>     # stop the process the watch claim names
squeezr fleet restart <storeKey>  # restart the watch daemon
```

- `stop` follows the claim (070/T5): where `watch.owner` is present it is the
  only name the kill surface reads — the claimed process is stopped (a wedged
  owner too; stopping the named process is the documented recovery for a
  store whose lock never came back), a claim held on another host is refused
  with a hint, a gone owner leaves nothing to stop, and live observers are
  never a target — the distinction is owner/observer, not client/daemon: the
  kill surface reads the claim holder whoever runs it (a client's live
  `serve --watch` is stoppable exactly because it holds the claim), while
  observers beat on the store unclaimed and stay untouched; the result
  counts them (additive `observers` in `--json`). With no claim marker at all
  the store is in pre-070 shape and the per-kind heartbeats decide, as they
  always did. The pid is verified before the signal either way: the process
  must look like squeezr (name/command line + a start-time cross-check
  against the marker's or heartbeat's `startedAt`) — a foreign process or a
  reused pid is untouched, you get a clear error. Nothing alive —
  `not-running`, exit 0.
- `restart` works with `watch` only: a live watch owner is stopped cleanly, then a
  detached `squeezr watch` process is spawned with cwd = the workspace's
  rootPath (rootPath gone — an error with a `drop` hint). A claimed
  `serve --watch` is not restarted and not stopped: the MCP client owns it,
  and trying honestly errors with a hint naming the owner pid (`squeezr mcp
  install` or an agent restart); beside an owner or a gone claim, observers do
  not block the restart.
- Both commands do `--json`: one NDJSON result object, clean stdout (the 035
  contract).

## Watchd: the global watch supervisor

A `squeezr watch` process lives as long as whoever started it — an agent
session, a terminal, an MCP client. `squeezr watchd` is the answer to "and who
brings the watcher back": one long-lived supervisor process per machine that
owns the watch daemons of every workspace in the registry and applies a
desired state per store (`running` / `stopped`), persisted across watchd
restarts in `~/.squeezr/watchd/state.json`.

```bash
squeezr watchd                    # start the supervisor (singleton per machine)
squeezr fleet stop <store-key>    # desired=stopped: the daemon goes down and stays down
squeezr fleet start <store-key>   # desired=running: watchd brings it back
squeezr fleet restart <store-key> # restart now, reset the wedged flag
squeezr fleet status              # desired vs actual, supervised/wedged/managed elsewhere
```

The same actions live in the TUI under `Watchd supervisor` — a session list
with per-session start / stop / restart.

Supervision rules, in one place:

- **Crash vs intent.** A watch process that dies is restarted (poll interval
  2s). Killing the process outside `fleet stop` counts as a crash — watchd
  brings it back; `fleet stop` is the way to say "keep it down".
- **Binary upgrades self-apply.** When the squeezr binary on disk becomes
  newer than the running watcher (version drift), watchd restarts it — after
  an upgrade the new code picks up without a manual step. Without watchd the
  upgrade flow is `squeezr fleet restart`.
- **Crash-loop guard.** More than 5 restarts of one store within 10 minutes
  wedges it: restarts stop, the status shows `wedged`, `fleet restart` resets.
- **A second watchd refuses to start** — the lock names the running instance.
- **Live foreign owners are respected.** A store watched by an agent session
  or an MCP client reads `managed elsewhere`: watchd does not touch it.

One nuance to know: a watch claim is intentionally held for a grace window
(30s) after its owner dies — a fresh beat from a pid that just died keeps the
claim until the window passes, so a takeover race cannot steal it. Supervised
watchers wait that window out (`--await-claim`) and take over when it expires,
so kill→respawn converges within ~30s. A manual `squeezr watch` started inside
the window declines immediately, as before.

### Autostart: `watchd install` / `uninstall` / `installed`

A manually started watchd lives and dies with the shell that started it. Three
verbs hand its lifetime to the operating system, take it back, and report what
actually happened:

```bash
squeezr watchd install     # register autostart for this account (idempotent)
squeezr watchd installed   # read-only verdict: is it registered, and will it start?
squeezr watchd uninstall   # remove the entry squeezr manages; a no-op when there is none
```

What `install` writes, per platform:

| Platform | The entry | Start semantics |
|---|---|---|
| Linux | `~/.config/systemd/user/squeezr-watchd.service` (an absolute `XDG_CONFIG_HOME` moves it, same as systemd does) | `systemctl --user enable --now` at install; `Restart=on-failure`, `RestartSec=5` in the unit |
| macOS | `~/Library/LaunchAgents/dev.summonasync.squeezr-watchd.plist` | `launchctl bootstrap gui/<uid>` at install; `RunAtLoad` + `KeepAlive`/`SuccessfulExit=false` in the plist |
| Windows | a Task Scheduler task `squeezr-watchd` created with `/SC ONLOGON` (deliberately an ONLOGON task, not a service) | fires at the next logon and nothing else — no restart-on-exit semantics at all |

The command line baked into every entry ends with `watchd --autostart`. The
flag is a declared option of the `watchd` command that tells the supervisor the
init system started it and will bring it back; that pair is what makes the
upgrade policy below possible. A manually started `squeezr watchd` (without the
flag) never retires itself.

**Upgrade retire.** Under `--autostart`, once per sweep the supervisor compares
the modification time of its own binary against its own process start time.
When the disk is newer — an upgrade landed — the drift check is the first
thing the next sweep runs: it prints `watchd is retiring after an upgrade:
<path> changed at <stamp>, this process started at <stamp>`, and exits **75**
at the top of that sweep without touching the fleet; the retire only unlocks
once at least one full useful fleet sweep has already completed in this
process's lifetime. 75 is sysexits'
`EX_TEMPFAIL`: a temporary failure the starter is invited to retry, and
`Restart=on-failure` (systemd) and `KeepAlive`+`SuccessfulExit=false` (launchd)
both bring the process back on any non-zero exit. The number cannot be 0 —
that reads as "finished cleanly, don't restart", and the fleet freezes on the
old code — and cannot be 1/2/3, because those steps belong to the
`watchd installed` verdict ladder. On Windows the retire question does not
arise: `/SC ONLOGON` never brings a process back, so the policy refuses
self-exit there and the running supervisor keeps its binary until the next
logon.

**What an upgrade does to the supervised daemons.** The generated systemd unit
leaves `KillMode` at the systemd default — `control-group` — so retiring the
supervisor stops its whole cgroup: every supervised watch daemon takes a
fleet-wide SIGTERM, roughly 5–10 seconds pass (`RestartSec=5`, then the new
instance's first sweep, which runs immediately at start — the 2 s cadence
never waits before relaunching; on launchd the default throttle caps the same
window at 10 s), and the whole fleet comes up on the new binary. On launchd
the children survive the retire
(the default `AbandonProcessGroup` keeps them), and the new supervisor reads
them as `SkippedLiveOwner` — `watch <store> is managed elsewhere (pid N)` — and
does not restart children it does not own: the fleet runs a mix of old and new
daemons until each survivor dies on its own. Both shapes are deliberate
(review-110-08 owner rulings), not accidents. One practical consequence: during
that 5–10 s idle window a starting `squeezr serve` sees the retired supervisor
as gone — the watchd lock is released and the claim's owner pid is not alive —
so it prints the plain no-indexer warning and asks the operator to run
`squeezr watch` by hand. That is the trap to know: the incoming supervisor
relaunches the daemon on its own within seconds, so the manual `watch` is
unnecessary here. The wedged-under-supervision warning (advice: "if the
supervisor does not clear it, run `squeezr fleet restart`") needs a live
supervisor and a live wedged owner, so it belongs to a genuinely wedged,
blocked store outside this window — and there that manual `fleet restart` is
exactly what it is for.

**Trap 1: linger on a headless machine.** A systemd *user* manager exists only
while the account has a session: without more, the unit will not start at boot
at all — it waits for the first login, and a logout takes the user manager (and
watchd) down with it. On a server you only reach over ssh, enable lingering
once:

```bash
loginctl enable-linger $USER
```

The account's units then start at boot and survive logout. (Platform behaviour;
owner-verifiable on the target box — after a reboot, `squeezr watchd installed`
should read `watchd=running`.) On macOS and Windows the entry is a plain
login-time agent/task and there is nothing to enable.

**Trap 2: the start limit parks a failing unit.** The unit names its own
window — `StartLimitIntervalSec=60` plus `StartLimitBurst=5` in `[Unit]`
(systemd's 10 s default cannot count a `RestartSec=5` flap) — so a binary that
dies at start is retried every 5 s, and after five starts inside 60 s systemd
stops trying: the unit comes to rest in `failed` instead of flapping forever.
That parked state is a *feature* against a broken binary — the crash loop has a
full stop. `squeezr watchd installed` reads it as `status=unit-failed` with
`failed=yes` and exit 2, and the hint says to look it up with the platform
tool. To recover: fix or replace the binary, then

```bash
systemctl --user reset-failed squeezr-watchd && systemctl --user restart squeezr-watchd
```

`reset-failed` flushes the counter instead of waiting for the declared 60 s
window to pass — until it does (or you reset), systemd keeps refusing the start
with "Start request repeated too quickly". Re-running `squeezr watchd install`
also works: it rewrites the unit and `enable --now` starts it again.

**`installed` is the instrument panel.** One `key=value` line per dimension —
byte-stable, scriptable — plus the exit-code ladder:

| Exit | `status=` | Means |
|---|---|---|
| 0 | `installed` | the entry is registered, hooked, and names a program this platform can exec |
| 1 | `not-installed` | nothing is registered |
| 2 | `installed-but-binary-missing` / `unit-failed` / `present-but-not-enabled` | registered, and it will not start the daemon as it stands |
| 3 | `probe-unsupported` / `probe-unknown` | the platform refused to answer, or has no question to ask here |

The other lines answer the neighbouring questions: `platform=`, `unit=` (where
the entry lives), `present=`/`enabled=`/`failed=`/`loaded=` (each `yes`, `no`,
`couldn't-ask` where the tool would not talk, or `not-supported` where the
platform has no such question — launchd for `failed=`, Windows for `loaded=`;
`failed=` also prints `not-supported` where the question exists but the
platform has no answer yet: a Windows task that never ran has no run outcome
to read, and systemd answers exit 4 for a unit its running manager has not
read),
`program=`/`arguments=`/`program-state=` (what the entry execs and whether it
can), `watchd=running`/`watchd=not-running` with `pid=` (the singleton lock —
registered and running are two facts, and a linger-less box after a reboot is
exactly where they disagree). `install` and `installed` deliberately stay
separate: the status command never fixes anything on its way past.

**`install` over a live manual watchd.** Before applying the entry, install
reads `~/.squeezr/watchd/watchd.lock` and warns (stderr) when a live instance
holds it — install takes nothing, steals nothing and kills nothing, because
whose instance owns the singleton is the operator's call. The warning names
what the entry will then do about the refusing instance: systemd restarts it on
failure until the start limit parks the unit in `failed`; launchd respawns it on
every unsuccessful exit; the Windows task's instance refuses at every logon.
Stop that instance and run install again. The success line is
`watchd autostart installed — <target>`, plus `(with SQUEEZR_HOME=<path>)` when
the installing shell had `SQUEEZR_HOME` explicitly set — only then is the
variable baked into the entry (the default home is whatever the account
resolves at logon anyway). `uninstall` prints `watchd autostart removed —
<target>`; nothing of ours to remove is not a failure either — it says `warning:
no squeezr watchd autostart entry at <target>; nothing was removed.` on stderr
and still exits 0.

**The release path.** Upgrading through `scripts/install-release.sh` keeps
autostart honest: the script restamps the unpacked binary with `touch` (a
published tar carries archived mtimes, and an unstamped file gives the drift
detector nothing to see), asks the new build `watchd installed`, and when
autostart is registered it rewrites the entry onto the new tag and restarts the
supervisor (`systemctl --user restart` on Linux, `launchctl kickstart -k` on
macOS). The step closes with a machine-readable line:
`autostart=rewritten+restarted` when the handover completed;
`autostart=rewired+restart-failed` when the entry moved but the restart was
refused (stderr names the manual restart commands); `autostart=touched` when
the probe gave no verdict, so the entry stays pointed where it was; and
`autostart=skipped:<reason>` (`no-new-build`, `touch-failed`,
`autostart-not-installed`, `install-failed`) when nothing needed rewiring.
Upgrading by unpacking a release tar by hand is the blind case — no restamp,
archived mtime preserved, so drift may never fire and the old supervisor runs
the old binary until you act: run `squeezr watchd install` from the new binary
(it re-points the entry — the unit names the concrete install path, not the
`~/.local/bin` symlink) and restart the entry yourself.

**Legacy LaunchAgent name.** During the pre-release drafts of this feature the
macOS file was `squeezr-watchd.plist`; current tooling writes and manages only
the reverse-DNS `dev.summonasync.squeezr-watchd.plist`. A machine that ran the
drafts can still carry the old file in `~/Library/LaunchAgents`, and neither
`uninstall` nor `install` touches it — it loads a second supervisor every login
(whose instance refuses the lock; launchd then respawns the refuser). If that
is your machine: `launchctl bootout gui/$(id -u)/squeezr-watchd`, delete the
old plist, re-run install. No released version ever wrote the legacy name, so
this paragraph is almost certainly not you. (Owner-verifiable on a macOS box
that ran feature-branch builds.)

## Vector hygiene: `squeezr vectors reconcile`

Routine cleanup is done by indexing: a file's chunk set changed — its old
points are deleted before the new ones are inserted. Deleting a file's points
is the job of the pass that carries the vector store; a watch pass does not
carry it, so such a deletion is recorded into the `pending_reaps` journal and
finished by the next `squeezr index` with embeddings. The journal is keyed by
more than the file id: `MetaStore` issues ids as `max(_id)+1`, meaning a freed
id goes to the next added file, and replaying by that one id would delete the
points of an already-live file — a row is owned by the pair (repo, path), and
if the id answers to another file the row is dropped, with the orphaned points
left for `reconcile` (it compares hashes, not ids). But a collection built
before this machinery carries orphan points: chunks long gone from `meta.db`
while the vectors lie on. `reconcile` clears that yard in one pass.

```bash
squeezr vectors reconcile            # dry-run: count and show, touch nothing (the default)
squeezr vectors reconcile --apply    # delete orphans + reset chunks whose point disappeared to pending
squeezr vectors reconcile --json     # the same report, machine-readable (one object, camelCase)
```

The mechanics: scroll all ids of the collection → compare against the full
set of live chunk hashes from `meta.db` → a point lives only if its id equals
`ChunkPointId.FromHash(h)` for some current hash. Compared by id, not by
payload `fileId`, so identical text in two files (points collapse into one,
the payload keeps one of the files) never loses a vector: the hash lives —
the point lives. The second front is the other side of the same collapse:
a repo of one file deletes the shared point while the second file's row stays
marked `ok` with no vector behind it; no following pass ever sees such a row,
so `--apply` resets it to `pending` and `squeezr index` re-embeds it (in
`--json` — `staleMarkers` in hashes and `requeued` in rows; the units differ,
hence the different names).

```
Collection squeezr-d23cb38850eb: 7492 → 6172 point(s) · 6707 chunk(s) · 6301 distinct chunk hash(es)
Deleted 1320 orphan point(s).
129 chunk(s) have no point yet — never embedded.
Re-queued 2 chunk(s) whose vectors are gone — run 'squeezr index' with embeddings available to re-embed them (watch passes do not embed).
```

Safety rails: without `--apply` nothing is written — no points are deleted,
no markers in `meta.db` are reset (`--dry-run` together with `--apply` is an
`invalid_usage` error); no `meta.db` — a refusal with code
`index_not_built`; Qdrant not answering — an error before any reads; zero
chunks in the index while the collection has points — a refusal to count
everything an orphan (`squeezr index` first); the pass takes `index.lock`, so
a parallel indexing cannot rewrite chunks under the comparison. The run is
idempotent: a second run finds 0 orphans. Reconcile is deliberately not built
into indexing — scrolling the whole collection on every pass would be
O(corpus) for the sake of a legacy problem.

Separately, the `index_status` tool (MCP) hints when there are noticeably
more points than chunks: the warning `the collection carries N more point(s)
than the index has chunks …` — a reason to run `reconcile`. Orphans come not
only from legacy: a repo deletes the second file's shared point (see above),
and a journal row whose id already went to another file is dropped — both
stashes are swept exactly by `reconcile --apply`; every other point-removal
path either does it or journals it and reports the count.

## Doctor: the checkup to run before blaming search

`squeezr doctor` walks every registered workspace (root gone, index missing or
never run, repos stale), the daemon heartbeats, the services, and the embeddings
chain itself. A workspace that indexed chunks but never embedded them does not
read healthy anymore: `semantic coverage 0%` fails with the remedy
`squeezr index — then check the embeddings provider line`, a down qdrant fails
with `squeezr services up`, and a pending backlog above one watch drain pass
(256 chunks) fails on its own. The resolved provider outcome surfaces as an
`embeddings provider` finding: a workspace still configured for the removed
onnx provider fails with the migration path, and an ollama model running
CPU-only is an info that says embeddings will be slow.
Exit code 1 when anything failed; `--json` prints one
`{kind, healthy, checks}` document; the run is read-only — findings carry
remedies, nothing is ever changed.

## Stats: token savings in numbers

"But does it actually save?" — `squeezr stats` answers with numbers, not
promises. Every navigation command (CLI: `search`/`symbol`/`refs`/`map`/
`expand`; MCP: all seven tools; `hook pre-read` also writes a row) appends one
telemetry line to `queries.jsonl` in the workspace store, and stats rolls it
into a summary. MCP answers
carry the same arithmetic in their own envelope: the additive `cost` block
(`tokensUsed`, `baselineTokens`, `savedTokens`, `ratio`, `complete`) shows
the whole-file price of the returned files at the moment of the call, and
telemetry records that same per-call `baselineTokens`, so the dashboard
and the transcript cannot disagree. `tokensUsed` there is the content the
`maxTokens` budget governs; the freshness story shipped beside it prices
itself in `freshness.freshnessTokens`, and the telemetry line records the sum.
`complete` is a claim about the verdict, not about the budget: it reads false
when `staleFiles` carries a sample of a bigger verdict, and a reader who sees
`complete: false` with `truncated: false` should go to `freshness`, not raise
`maxTokens`:

```bash
squeezr stats           # per-tool table + savings + cache
squeezr stats --json    # one JSON object (the 035 contract)
squeezr stats --reset   # clear the telemetry
```

```
Tool            Count  Avg tokens  Truncation  Mapped
code_pack       4      249.5       50.0 %      100.0 %
find_references 1      0.0         0.0 %       0.0 %
map             1      147.0       0.0 %       0.0 %
pre-read        1      0.0         0.0 %       n/a
refs            1      0.0         0.0 %       n/a
symbol          5      612.6       40.0 %      40.0 %
symbol_lookup   15     464.6       53.3 %      77.8 %

Mapped rate covers 18 of 28 row(s): 10 row(s) carry no state, recorded before the clipped/mapped split or by a surface that has none.
Savings: query cost 11177 · whole-file baseline 189849 · saved 178672 (×17.0)
Cache hit-rate: 10.0 % across 1 session(s) over 20 cache-eligible queries
Period: 2026-11-10 18:39 — 2026-11-10 18:49 (28 records)
```

- **The per-tool table** — calls, average `tokensUsed`, the share of clipped
  answers and the share of mapped ones. `Truncation` divides ALL of the tool's
  rows; `Mapped` divides only the rows that recorded a state, so the two
  columns are not comparable row by row — the `symbol_lookup` row above is 8 of
  15 clipped against 7 of 9 known mapped, and reading `100 %` as "nothing was
  clipped" would be wrong. `n/a` means the tool has no known row at all (a rate
  over an empty denominator would say "nothing was mapped" where the ledger
  says "nothing is known"), and the grey footnote names how many rows sit
  behind those cells. The same table renders in the interactive TUI stats view
  — `TuiStatsView` calls the shared `StatsCommand.Render` — so one description
  covers both surfaces. In `--json` the same numbers arrive as two more members
  of each `byTool` row, last in the row: `mappedRate` (absent when the tool has
  no known row — the `n/a` cell) and `unsplitRows` (always present, `0` in the
  clean case). No new top-level member: the `stats --json` key list stays
  `{totalRequests, byTool, savings, cache, period, agents, sessions}`.
- **What `mapped` means in the ledger** — `queries.jsonl` rows gained an
  additive `mapped` as their LAST key: `true` when the budget traded a body for
  the full member map (the answer lost nothing), `false` when it did not. Every
  `QueryTelemetry.Record` site in `src/` must name its state — omitting
  `mapped:` fails the build (`QueryTelemetryRecordStateGuardTests`), so a
  missing key on the wire never means "a call site forgot". A row with no
  `mapped` key is **unsplittable**, and there are two origins for that: written
  before the split shipped, or written by a surface that records no budget
  state at all (`hook pre-read` passes `mapped: null` explicitly; CLI `refs`
  names no state and is the guard's one declared exception — and the wire
  cannot tell them apart, because a null is dropped on serialization). Known
  asymmetry, not papered over: the MCP `find_references` row writes
  `mapped: false` while the CLI `refs` row of the same logical query writes
  nothing, so one surface has two ledger readings; the guard records that
  choice, it does not fix it. One more denominator caveat, unrelated to the
  split but true of every number on this page: a call that ends in an error
  writes no row at all (`BUGS/026`), so these rates describe answers that
  worked, never the traffic an agent actually generated.
- **Savings** — a counterfactual against the whole-file baseline. The formula
  (unchanged since specs 021/050):
  - `actual` (query cost) = the sum of `tokensUsed` over all answers;
  - `counterfactual` (whole-file baseline) = the sum of token estimates of
    the FULL files opened via `expand`/`expand_span` — a file counts once,
    the size-based estimate is the same formula as the chunker's; files no
    longer in the index are honestly excluded with an `unknownFiles` line;
  - `saved = max(0, counterfactual − actual)`, `ratio = counterfactual /
    actual`.

  The baseline only means something when it outweighs the actual cost
  (`counterfactual > actual`) — then the line reads `Savings: query cost X ·
  whole-file baseline Y · saved Z (×N)`. Otherwise (a search-centered mix
  where `counterfactual ≤ actual`, or no expands at all) it prints `Savings:
  no whole-file baseline for this usage mix (query cost X; the baseline
  counts whole files read via expand only)` — no meaningless `saved 0 (×0.3)`
  next to two non-zero numbers: `saved` and `×N` print only in the first
  branch. In `--json` `savings.baseline` (the baseline label) and
  `savings.baselineApplies` (bool — whether it outweighs the query cost) are
  added additively; existing fields are byte-compatible (the 035 contract).
- **Cache hit-rate** — the share of answers replayed from serve-session
  caches (see the envelope semantics) and the number of sessions where the
  cache fired; CLI runs use no cache — every run is its own session. With no
  hits at all the line does not lie: instead of a contradictory `0.0 % across
  0 session(s)` next to a live By session table, it prints `Cache hit-rate:
  no hits yet across N recorded session(s)` — N matches the session count
  below — and if the index killed the cache, it adds `— the index changed K
  time(s), last at <ts>`. In `--json` `cache.invalidations` and
  `cache.lastInvalidation` are added additively (the 035 contract:
  `lastInvalidation` is absent until the first invalidation).
- **Period** — the window of records and their count; over 50 000 records —
  a grey hint toward `--reset` (no auto-cleanup, deliberate).
- **By agent** — who called the tools: MCP clients by name and version from
  the initialize handshake (`kimi-code 1.2.0`, `claude-code 2.0.5`), CLI runs
  as `cli`, records from before the field — `unknown`.
- **By session** — the per-session breakdown (one serve session = one client
  connection): short id, client, query count, last-seen and the top-3 tools —
  you can see "this session lived three hours and made two code_search
  calls".

```
By agent
Client       Version  Count  Last seen
claude-code  2.0.5    3      2026-09-25 10:05
kimi-code    1.2.0    3      2026-09-25 09:15
cli          -        1      2026-09-25 11:31
unknown      -        1      2026-09-18 08:00

By session
Session   Client       Count  Last seen         Top tools
c0ffee00  cli          1      2026-09-25 11:31  search
77aa22bb  claude-code  3      2026-09-25 10:05  expand_span, find_references, repo_map
3f8a1c2e  kimi-code    3      2026-09-25 09:15  symbol_lookup, code_search
deadbeef  unknown      1      2026-09-18 08:00  search
```

Privacy: the telemetry is a local `queries.jsonl` in the workspace's store
dir (`~/.squeezr/workspaces/<storeKey>/`). It is not in the project, not in
git, never leaves the machine, and dies with the workspace — `squeezr drop`
removes it with the whole store.

## Usage: adoption across agents, projects and sessions

`stats` covers one workspace, while `squeezr usage` rolls the telemetry up
across every workspace in the registry — which agents actually work in which
projects (the command needs no current workspace and runs from any directory,
like `fleet`):

```bash
squeezr usage                      # rollup over all workspaces + a final by-agent table
squeezr usage --agent kimi-code    # one client (exact match, case-insensitive)
squeezr usage --workspace <key>    # one workspace by storeKey
squeezr usage --since 7d           # a time window: 90m / 24h / 7d / 30d
squeezr usage --json               # one JSON object (the 035 contract)
squeezr usage --watch              # a live refresh (3 s, --interval N), Ctrl+C — a clean exit
```

```
demo-proj-a-c4654229 — 8 record(s)
  /tmp/demo-proj-a
demo-proj-b-54669aef — 3 record(s)
  /tmp/demo-proj-b
                      By agent
Client       Version  Count  Last seen
kimi-code    1.2.0    5      2026-09-25 09:15
claude-code  2.0.5    3      2026-09-25 10:05
cli          -        2      2026-09-25 11:31
unknown      -        1      2026-09-18 08:00
Period: 2026-09-18 08:00 — 2026-09-25 11:31 (11 records)
```

- Filters combine (AND): `squeezr usage --agent kimi-code --since 24h` — only
  kimi for the last day.
- A workspace with a vanished rootPath is marked `(stale)` — the command does
  not crash (best-effort, like `fleet`).
- `--watch` and `--json` are incompatible (json is never live) — an error per
  the 035 contract; in a pipe (non-TTY) `--watch` prints one snapshot with a
  stderr warning and exits.
- No records at all — an honest `No usage telemetry yet.`
- Retention is the same: over 50 000 records total — a hint to run `squeezr
  stats --reset` across the workspaces.

## Roadmap

- [x] **M1** — CLI skeleton, LiteDB schemas, config, workspace
- [x] **M2** — trigram index, exact `search`, incremental, `compact`
- [x] **M3** — Roslyn: symbols, chunks, `symbol`, `map`, `expand`
- [x] **M4** — Qdrant + ONNX embeddings: hybrid `search`, `refs`
- [x] **M5** — MCP `serve` with all tools + agent-facing README
- [x] **M6** — tree-sitter for JS/TS, the fallback chunker

## Stack

.NET 10 / C# 14 · Spectre.Console.Cli · LiteDB · Roslyn · tree-sitter (JS/TS)
· Qdrant · Ollama · xUnit + Shouldly

---

*summonasync — one dev, a swarm of agents, nothing leaves the machine.*  
overmind · grimoire · squeezr · mindlash studio

Everything here runs on your machine: the index lives in `~/.squeezr`, the
telemetry never leaves it, and it dies with the workspace. If it does not —
that is a bug, file it.
