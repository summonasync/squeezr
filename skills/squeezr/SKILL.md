---
name: squeezr
description: Set up and operate squeezr, a local hybrid (exact + semantic) code index served to agents over MCP - install the CLI, start the Qdrant/Ollama services, init and index a workspace, register the MCP tools, then query code with code_search, repo_map, expand_span, index_status instead of reading whole files. Use when asked to use or install squeezr, bring up / raise the code index, onboard onto a repository, or do token-cheap code search.
---

# Squeezr — agent onboarding and code search

## 0. Principle: only on explicit request

Install or reconfigure squeezr ONLY after the human explicitly asked for it ("use
squeezr here", "set up the index"). Never onboard a machine proactively and never
install it as a side effect of another task. Sections 1–4 run only in that case.
Section 5 applies whenever the squeezr MCP tools are already registered.

## 1. Install (linux-x64)

Check first; if this prints a version, skip to section 2:

```bash
squeezr --version
```

If missing, install the latest published release (no sudo, user dirs only):

```bash
curl -fsSL http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr/raw/main/scripts/install-release.sh | bash
```

The script resolves the latest Gitea release, unpacks into `~/apps/squeezr/<tag>/`
and repoints the `~/.local/bin/squeezr` symlink, then prints the installed version.
If `~/.local/bin` is not on PATH, add it to the shell profile and open a new terminal
before re-checking. Pin or roll back with `--version <tag>` (e.g. `v1.0.0`); force a
re-download of an installed tag with `--force`. The script is the single source of
install truth — install evolution changes the script, never this skill.

## 2. Services (Qdrant + Ollama)

```bash
squeezr services up
```

Starts the managed docker compose stack (Qdrant + Ollama); the first run pulls
~3.6 GB of images. On success it prints the endpoints — Qdrant has two ports,
only one of them is for squeezr:

- Qdrant gRPC `http://localhost:6334` — what `qdrant.endpoint` must point at
- Qdrant REST `http://localhost:6333` — dashboard/API only, never for squeezr
- Ollama `http://127.0.0.1:11434`

The embedding model needs no manual step: the ollama provider pulls its model
automatically on first `squeezr index`. New workspaces default to
`embeddings.provider: "auto"`: every indexing run requires the ollama endpoint
to answer — a no-answer run fails with an explicit `squeezr services up` hint.
No provider configuration needed.

If docker is missing or the daemon is down, do not install docker and do not
improvise — stop and ask the human how they want the services hosted.
`squeezr services status` shows container state; `squeezr services down` stops both
(data volumes are kept).

## 3. Workspace: init → add → index

Run from the project root; each project gets its own workspace:

```bash
squeezr init
squeezr add .
squeezr index --events
```

- `init` creates the workspace in the current directory with default config.
- `add <path> [--name <repo>]` registers a repository; the index stays empty until
  at least one repo is added. `add .` covers the common single-repo case (the
  `.squeezr` store dir is always skipped). Sibling repos join the same workspace
  with `squeezr add <path> --name <repo>` (absolute path or one relative to the
  workspace root), and the `repo` parameter of the search/map tools then narrows
  a query to a single repository.
- `index` builds the trigram + symbol index and embeds chunks for semantic search.
  `--events` streams machine-readable progress events instead of the interactive
  progress bar. Other switches: `--full` (complete rebuild), `--no-semantic`
  (literal-only), `--reembed` (re-embed everything after a model change).
  Symbol-aware languages: C# through Roslyn; JavaScript, TypeScript, TSX, Rust, Python and
  Go through tree-sitter. Rust answers with functions, structs, enums, unions, traits,
  impl blocks, modules, type aliases, consts, statics and macro definitions as
  symbols, and with `use` trees, calls, trait/impl targets and macro invocations as
  references; its `impl<T> path::Type<T>` targets resolve by the bare type name, so a
  generic or qualified impl block still lands on the row the struct owns. Trait-impl
  blocks carry the composite row name `Trait for Type`, and `symbol_lookup` on a type
  answers its struct row plus any inherent-impl rows of the same name — the impl is a
  row, not a member of the struct row. Python answers with functions (an `async def` is
  one function row that keeps the keyword in its signature), classes, methods and
  UPPER_SNAKE module or class constants; a decorator belongs to the definition under it,
  so the row's span starts at the `@` line while its signature stays on the `def` line.
  Its references are imports, calls and base classes, and the import rule is python's
  own: `import a.b.c` binds the TOP package `a` and keeps the whole path in the module
  column, `import a.b.c as d` binds `d`, `from a.b import c` binds `c`, `from x import *`
  binds the module `x` itself and no individual names behind the star, and a relative
  form keeps its column (`from .text import x` says `.text`) — so grepping the leaf of a
  dotted import finds nothing, because the importing file never has that name. Go answers
  with functions, methods, structs, interfaces, type declarations, aliases and
  package-level consts and vars, and a method is named by its receiver: `func (r *Registry)
  Add` is owned by `Registry`, which is also how `symbol_lookup` pairs the two rows — the
  same for an unnamed receiver (`func (*Registry) All`) and a generic one
  (`func (g *Generic[T]) Get` → `Generic.Get`). Its references are imports, calls, type
  uses and embedded fields, and `import "net/http"` binds the LAST element (`http`) with
  an explicit alias winning over it, so the two grammars keep separate import rules
  instead of one shared guess. What the three do not ship: a go blank identifier (`_`), a
  multi-name const/var spec, a go const or var written inside a function body, and doc
  comments — rust `///`, python docstrings and go `//` are indexed as text, not as
  documentation. Anything
  else (Markdown, YAML, JSON, plain text) gets the sliding-window fallback chunker —
  searchable, never symbol-aware.

`squeezr status` shows per-repo file/symbol/chunk counts, the configured
embeddings provider and Qdrant state.

Doc-heavy repos can curate ranking without unindexing anything: set
`indexing.rankExclude` in the workspace config
(`~/.squeezr/workspaces/<storeKey>/config.json`) to a list of globs, e.g.
`["audits/**", "BACKLOG/**"]`. Matching files stay indexed and fully navigable —
`expand_span` / `find_references` / `symbol_lookup` still reach them — they just
stop outranking source in `code_search`. It applies at query time, so no reindex
is needed; `prefix` narrows in, `rankExclude` pushes out, and the two intersect.
`index_status` lists the active patterns in `warnings`, and a malformed glob
warns and is skipped instead of failing the query. Query globs (`rankExclude`,
`pathGlob`) read the same `[abc]`/`[a-z]`/`[!…]` classes as the ignore dialect;
there `\` stays a directory separator rather than an escape, a class cannot
contain `/`, and there is no residual-form skip: an unterminated `[` or a POSIX
class is read literally instead of warned and dropped.

## 4. MCP registration

```bash
squeezr mcp install --all --scope user --yes
```

Registers the squeezr MCP server (stdio) in every MCP client detected on the
machine, at user scope, fully non-interactive (`--yes` without `--scope` also
defaults to user). The registration is a plain `serve`: it answers queries and
refreshes what a query touches, but runs no background indexer — since 070/T5
a watcher is a fleet decision (`squeezr watch`, or `--watch` at install to opt
one client in), not something every session breeds. After a successful
install, restart the agent session — MCP clients
load server configs at startup, not live. Six tools then appear: `code_search`,
`symbol_lookup`, `find_references`, `repo_map`, `expand_span`, `index_status`.
`squeezr mcp doctor` verifies the registration later — per store it names the
watch owner pid, counts live observers and, where nobody owns the loop, hands
the exact `squeezr fleet restart <storeKey>` that starts one; `squeezr mcp remove` undoes it.

Repo-level onboarding (only when the human explicitly asks for it): after MCP
registration, `squeezr mcp install --project` writes a committed
`squeezr:project-instructions` block into the project's own agent files —
`AGENTS.md` always (created when missing), plus `CLAUDE.md`/`QWEN.md` when they
exist and are not one-line `@AGENTS.md` import pointers. Without `--client`/`--all`
the command only writes the block, no client registration; plain `mcp install`
never touches project files. The block header carries the squeezr version —
re-run the same command after a squeezr upgrade to refresh it;
`squeezr mcp remove --project` strips the block from all carrier files (files
themselves are never deleted).

`squeezr mcp install` also arms the advisory PreToolUse hook in the same pass
that registers a client, for the two runtimes whose hook layout is verified:
Claude Code (`Read`, in `~/.claude/settings.json`) and Qwen Code
(`^read_file$`, in the same `~/.qwen/settings.json` that carries `mcpServers`).
Kimi Code and Codex CLI come back `not armed (hook layout unverified)` — no
path is invented for a layout nobody checked. `squeezr hook pre-read` reads the
payload from stdin and, when the read targets a large file or a wide range
inside a registered workspace, answers with
`hookSpecificOutput.additionalContext` pointing at `code_search` /
`symbol_lookup` / `expand_span`. That JSON shape is what puts the advice in the
model's context: bare text on stdout at exit 0 reaches the human as a UI
`systemMessage` and the model never sees it. Advisory only — it never sets
`permissionDecision` and never blocks a read. `tool_name` in the payload
carries the runtime's own id (Qwen sends `read_file`, Claude sends `Read`;
`Read`/`ReadFile` are accepted matcher aliases on Qwen while the delivered name
still says `read_file`), and the telemetry row keeps that value verbatim —
which is how `squeezr usage` names the runtime behind each reading session.

Check it with `squeezr mcp doctor`: one `hook pre-read: <state>` line per
client (`armed` / `armed (project scope)` / `not armed` / `not armed (hook layout unverified)`),
additive `hookStatus` in `--json`. `squeezr mcp remove` strips the
managed entry. A snippet applied by hand before this existed counts as managed
— install normalizes it to the canonical command, remove takes it back.
`mcp install --project` alone arms nothing, and there is no flag to skip the
arm. <removed 2026-11-11 — 114/T3>~~The wiring is human-applied — never edit
hook configs from an agent session. Other clients (qwen, kimi) can wire the
same pattern when their hook surface allows.~~</removed> Wiring is the
command's job now; still never hand-edit a hook config from an agent session —
run `mcp install` and read the verdict from `mcp doctor`.

## 5. Tool cheat-sheet (MCP)

Navigation flow: `repo_map` first for orientation → `code_search` /
`symbol_lookup` to locate → `find_references` before changing anything →
`expand_span` to read. Exact string search or a repo outside the index —
plain grep/read is correct, not a violation.

- `repo_map(repo?, prefix?, depth?, maxTokens?)` — directory tree with type/member
  signatures, no bodies. Cheapest first contact with an unfamiliar workspace.
  Pass `prefix` (e.g. `"src/"`) to render only that subtree — directory labels
  come back relative to the prefix and `depth` keeps its structural meaning.
- `code_search(query, repo?, prefix?, topK?, maxTokens?, kind?)` — hybrid exact + semantic search:
  paraphrases work ("validate config file") and so do exact identifiers
  ("VectorDimensionMismatchException"). Returns file:line matches with snippets,
  never full bodies. One hit = one file: the best span wins and the file's other
  spans collapse into `alsoMatched`, so `topK` caps distinct files, not spans.
  `prefix` scopes both arms to one directory subtree; the
  workspace config `indexing.rankExclude` keeps whole trees out of the ranking
  while they stay indexed and readable through `expand_span`.
  `kind` filters what a hit can be: `all` (default, unchanged results), `code`
  (program sources) or `docs` (markdown/text/json/yaml/toml/xml prose and
  config); it runs on both arms, and unknown values error with the allowed list
  instead of silently meaning `all`. Points without a `kind` payload (orphans,
  or points from pre-057 binaries until the backfill converges them) match only
  `kind=all`.
- `symbol_lookup` — exact symbol lookup: signature, doc-comment, file:span. When a
  type's body exceeds the token budget it returns the member list (name, kind, line)
  as a mini-map instead — each line is an expand_span coordinate. That substitution
  is a *mapped* answer, not a cut one: `truncated: false`, `cost.complete: true`, and
  the hit says `bodyOmitted: true` (see Budgets below — do not re-ask at a bigger
  ceiling for a body the map already replaced). When no symbol
  matches, returns `didYouMean` (top-3 candidates by trigram-Jaccard, threshold
  0.34) instead of a bare miss.
- `find_references` — caller-side reference points for a symbol.
- `expand_span(file, startLine, endLine?, contextLines?, maxTokens?)` — the only
  tool that returns raw source lines; reads the live file on disk, so it reflects
  edits newer than the index. Call it on coordinates from other tools.
- `index_status()` — workspace freshness: per-repo counts, pending embeddings,
  semantic coverage (`embeddings.coverage`, embedded share of chunks 0..1), the
  embedding identity (`embeddings.modelKey` — the model whose vectors are in this
  store — with `embeddings.otherModelChunks`, how many embedded chunks carry a
  different one; both absent when the provider does not resolve, and reading them
  costs one bounded provider probe per call: ~5 s worst case under `auto`, ~2 s
  explicit ollama, 0 s OpenAI-compatible),
  per-kind chunk counts (`kindChunks`: `code`/`docs`, values sum to the per-repo
  chunks counts), Qdrant availability, and the resolved workspace `rootPath` (if it is not your
  project's root, the session serves a different workspace). Call before heavy
  querying to decide on a reindex. When ranking is curated, `warnings` lists the
  active `indexing.rankExclude` patterns — read them before trusting a
  `code_search` result set as complete. When files are excluded from indexing,
  the `ignored` block per repo shows `{ files, bySource }` (sources: defaults,
  config, squeezrignore, gitignore — absent keys mean 0) and `warnings` names
  the dominant source with a hint on how to disable it. `files` counts files,
  not directories: everything under a pruned directory (`out/`) is included,
  counted by name only. The ignored numbers are a snapshot from the last index
  pass, not live disk state — they stay stable until the next `squeezr index`.
  Additive to the same contract: when the registry holds two or more workspaces,
  `data` also carries `workspaces` — `{ name, rootPath, storeKey, current, stale }`
  rows, this session's listed first, at most 12 of them with the rest counted in
  `workspacesOmitted` — so you can tell a wrong-scope answer from an absent one
  (`squeezr add <path> --name <repo>` joins a sibling repo into this workspace).
  A single-workspace machine gets neither key, and its `data` is byte-identical
  to the pre-`workspaces` payload.

Budgets: every tool is token-budgeted, and `truncated` now means what it says —
the answer lost something you asked for. `symbol_lookup`/`code_pack` distinguish
three states: **complete** (nothing refused), **mapped** (the type's body was too
big and the full member map stood in for it — `truncated: false`,
`cost.complete: true`, no warning, the hit carries `bodyOmitted: true`; this is
the designed outcome, do NOT re-ask for a bigger ceiling to "get the body" — the
body is what the map replaced, and a map row is an `expand_span` coordinate),
and **clipped** (a hit dropped, map rows omitted, a `did you mean` refused, or a
body refused with nothing to substitute — only this earns a `truncated:` warning
naming the loss). The clipped warning line prints
`truncated: {used} of {ceiling} token ceiling — {remedy}`, and for the symbol family
(`symbol_lookup` plus the lookup leg of `code_pack`) the remedy is chosen
by what was refused: `narrow the query (repo, kind) or raise maxTokens` (a hit
dropped), `read a member with expand_span or raise maxTokens` (map rows omitted),
`raise maxTokens (the body alone needs ~N tokens)` (body refused, no substitute).
The CLI spells the same three in flags:
`narrow the query (--repo, --kind) or raise --max-tokens`,
`read a member with expand <file> <line> or raise --max-tokens`,
`raise --max-tokens (the body alone needs ~N tokens)`.
Outside that family two more texts exist: `expand_span` says
`raise maxTokens, or narrow the span (startLine/endLine)`, and a zero-hit answer whose
`did you mean` list did not fit falls back to the bare `raise maxTokens` (suggestions
dropped, answer clipped).
Read the state through `truncated` plus the hit's
`bodyOmitted`/`members`/`membersOmitted`, never through the absence of a `body`
key (nulls are dropped on serialization, so a mapped hit has no `body` field at
all). `raise maxTokens` in a remedy is a ceiling choice that re-runs in your
next call on the same connection: the resolved ceiling is part of
`symbol_lookup`'s response cache key (`BUGS/024`, closed by 115/T7), so a bigger
ceiling answers with its own envelope; only an identical ceiling replays.
`code_search` defaults to
maxTokens 2000 and sets `truncated` when it overflows — narrow the query or
adjust maxTokens instead of reading files. A member map is not authoritative
while `truncated: true`: an empty map still reports nothing omitted
(`BUGS/023`). In big workspaces prefer `prefix` over raising
maxTokens: a scoped map or search usually fits the default budget. Pass `topK`
to cap how many files come back when a few strong hits are enough — `code_search`
returns one hit per file, so `topK` counts distinct files, not spans. The
ledger records the split too (`mapped` on every `queries.jsonl` row that has a
budget state): `squeezr stats` shows a `Mapped` column beside `Truncation`, the
two rates divide different denominators (all rows vs rows with a known state),
and a missing `mapped:` at a record site fails the build rather than silently
adding an unsplittable row.

Errors ride a second plane: when a call comes back `isError: true`, the machine
contract is in `structuredContent` — `{code, message, rootPath, nextSteps,
cause?}` — and `nextSteps` are exact commands, safe to run verbatim. On
`workspace_not_initialized` check `cause` first: `root_without_manifest` means
init+index the named root; `session_outside_root` means your session sits
outside that root — rebind the client instead of initializing a second store.

Freshness and cache: reads refresh modified files inline or report them in
`staleFiles` (then suggest `squeezr index`). Identical repeated calls within one
serve session come from an in-memory cache; a reindex invalidates it.

Every tool also has a CLI twin (`squeezr search`, `map`, `symbol`, `refs`,
`expand`) for use outside an MCP session.

### Secret guard: read a withheld result as a refusal, not as absence

Squeezr protects secret-shaped paths — 19 built-in masks (`.env` family,
`*.key`, `*.pem`, `*.pfx`, `*.p12`, `*.jks`, `*.kdbx`, `id_rsa*`/`id_dsa*`/
`id_ecdsa*`/`id_ed25519*`, `.npmrc`, `.netrc`, `.git-credentials`, `.pgpass`,
`secrets.json`, `secrets.*.json`, `*.secret`) plus whatever
`indexing.secretGuardExtra` adds. The decision is on the **path shape only**:
the file is never opened to classify it, never indexed, never rendered, and its
content never reaches a log, an error message or telemetry.

Two answer shapes, and neither one means "the file is missing":

- You named a protected path explicitly → a **tool error**, not a miss:
  `'.env' is protected by squeezr's built-in secret guard: never indexed, never
  rendered`. Do not fall back to reading the file yourself and do not conclude
  it does not exist — it exists, the index just will not serve it. A trailing
  separator (`deploy/keys/id_ed25519/`) is trimmed before the decision, so a
  directory-shaped path you copied still gets this refusal rather than "not in
  the index". The name quoted in the refusal is either the path you asked about
  or, on `find_references`, the file of the first protected declaration of the
  symbol you named (it is the first, not proof of being the only one) — refusing
  by name beats claiming the symbol does not exist. Note the refusal always
  says *built-in*, even when an `indexing.secretGuardExtra` mask decided the
  case; only the tally distinguishes origins.
- The guard silently dropped protected results inside an otherwise-successful
  answer → `data` is smaller than you expected and `warnings` carries one line:
  `secret guard: 3 result(s) withheld ('.env' matches built-in patterns)`. The
  name in that line is **the first result the guard dropped, not the path you
  asked for**: a `repo_map` scoped to a prefix names a file under it, an
  unscoped query names a protected path that never appeared in any argument.
  Treat a withheld line as leaking what it names — do not quote it into a
  report, a screenshot or a commit message. The origin suffix is either
  `built-in patterns` or `indexing.secretGuardExtra`.

What to do with that: treat the answer as **complete minus the protected part**.
Do not widen the query hunting for a "hidden" hit, do not read an empty
`symbol_lookup`/`code_search` with a `secret guard:` warning as evidence the
code is absent, and keep the hints in proportion: `didYouMean` never suggests a
name that lives only in a protected file, and the "No symbol found — use
code_search for paraphrase queries." hint is skipped when something was
withheld. A prefix or a whole repo
whose content is only secrets maps empty with a withheld count instead of
claiming the directory is bare. The same sentence rides CLI stderr, MCP
envelopes and the TUI, so counting it across surfaces is reliable — and when
nothing was withheld the human surfaces stay silent.

Ingestion counters say whether the guard also refused files *on the way in*
(last pass snapshot, not live disk state): `index_status` per-repo
`guard { withheld, withheldByBuiltIns, withheldByConfig, dropped }`, `squeezr
status` `Withheld`/`Dropped` columns, `index --json` `guardWithheld` /
`guardWithheldBuiltIns` / `guardDropped` at the root and per repo. In the
`--events` root `completed` line only `guardWithheld` and `guardDropped` are
present — `guardWithheldBuiltIns` is per-repo in `repos[]`. A refusal counts as
skipped, never as a failed index pass.

## 6. Ignore mechanics

Squeezr excludes files from indexing via a layered ruleset (last-match-wins):

1. **Built-in defaults** — `node_modules`, `bin`, `obj`, `dist`, `.next`, `.vs`,
   `coverage`, etc. Always active.
2. **`indexing.ignore`** in the workspace config — additional globs.
3. **`.squeezrignore`** in the repo root — gitignore-syntax, non-cascading.
4. **`.gitignore` cascade** — enabled by default (`indexing.respectGitignore:
   true`). Set to `false` to disable.

Supported gitignore subset: `#` comments, `!` negation, `/` anchoring, trailing
`/` for dirs, `*`, `?`, `**`, `[character-classes]` — sets `[abc]`, ranges
`[a-z]`, negation with `[!…]` or `[^…]` — and `\` escapes that take the magic
out of `* ? [ ]` (and make a literal of space, `#`, `!`). Since 063/T14 these
are matched, not skipped; the residual forms that still warn and get skipped
are an unterminated `[`, POSIX `[:alpha:]`-style classes — nested in a set too
(`[a[:digit:]b]` drops the whole pattern) — an escaped separator `\/`, an
escaped trailing space (`foo\ `: the line is trimmed before validation), and a
trailing `\`. The warning quotes the line exactly as the file holds it.

`code_search` hybrid gained an identifier-exact branch: multi-token queries with
code-shaped tokens (camelCase, `snake_case`, length ≥ 12) or rare tokens hit a
dedicated branch with `source=exact` — more precise for compound identifiers.
The branch honours the same filters as the phrase arm (`repo`, `language`,
`pathGlob`, `prefix`, `rankExclude`).

Known limitation of the branch, a quiet recall loss: case expansion of a token's
trigrams is capped (`CaseVariantCap = 64` divided by the token's trigram count,
8 variants per trigram), so a lowercase token longer than ~10 characters stops
folding case — `materialization` will not reach a spelling that exists only as
`Materialization…`, and the token drops out of the discriminants silently. The
phrase arm degrades to a disk full-scan in the same case, so single literals keep
their recall. The real fix is case-folded trigrams in the index — an index format
change that needs a rebuild and has no owner in the backlog yet.

### `indexing.secretGuardExtra` reads the ignore dialect, and subtracts nothing

`indexing.secretGuardExtra` (array of globs in the workspace `config.json`) is
the only guard knob, and it is **union-only**: your masks are checked first, the
19 built-in masks always apply, nothing can shrink the built-in set. A shape
that names no file is dropped when the extras are compiled — `!negation`,
`#comment`, a dir-only `secrets/`, an unsupported residual form — so a negation
is **inert by construction** rather than "un-applied later": it cannot start
meaning "unprotect". Character classes and `\` escapes (063/T14) can only widen a
mask, never narrow it.

The dialect is the same gitignore subset as `indexing.ignore`, and it means the
same thing: an unanchored pattern matches at **any depth** (`*.tokens` covers
both `root.tokens` and `src/deep.tokens`), an internal `/` anchors at the repo
root (`deploy/*.crt` covers `deploy/server.crt` but **not**
`deploy/sub/deep.crt`), `**/tokens.txt` covers any depth but not
`tokens.txt.bak`. To cover a whole subtree, write a file-shaped mask with a
`**/` prefix (`**/server.crt`) or an anchored mask per depth (`deploy/*.crt`) —
a bare trailing-slash directory (`secrets/`) names no file, so it is dropped as
inert and protects nothing.

Two boundaries of the classifier, so you do not misread a result as a leak:

- It decides on the **last path segment**. A path where `.env` is a *directory*
  (`.env/dev`, `.env/local.json`) is therefore not protected — while a file with
  a secret-shaped name inside it (`.env/prod.key`) still is. A trailing
  separator is trimmed before the decision, so a copied directory-shaped
  `.env/` still refuses rather than reporting "not found".
- `id_rsa*` matches public keys too: `.ssh/id_rsa.pub` is protected alongside
  the private half. Over-protection is the chosen failure mode here.

## 7. Troubleshooting

- **qdrant.endpoint / gRPC vs REST** — a warning saying the configured endpoint
  "points at the REST port — squeezr speaks gRPC" means the workspace config has
  6333 where 6334 belongs. Fix `qdrant.endpoint` to `http://localhost:6334`;
  both endpoints are printed by `squeezr services up`.
- **dimension_mismatch** — "Qdrant collection ... vector size X, but the active
  embedding model produces Y": the embedding model changed. Fix is
  `squeezr index --reembed`; warn the human first, re-embedding a large
  workspace takes a while.
- **Tools missing in qwen** — some clients (qwen) deliver MCP tools deferred
  via `tool_search select`: the squeezr tools stay unloaded until the client
  activates them by name. That is the client's mechanism, not a broken squeezr
  registration (`squeezr mcp doctor` still reports the server healthy); check
  the client settings for auto-activation options.
- **Daemons** — `squeezr fleet` lists watch/serve-watch daemons across all
  registered workspaces (every live pid, the claim owner marked `(owner)`,
  uptime, staleness); `squeezr fleet stop <storeKey>` stops the process the
  watch claim names and never kills observers (they are MCP clients' live
  servers), `squeezr fleet restart <storeKey>` restarts the watch daemon
  (exact store key from the table).
- **Telemetry** — `squeezr stats` shows per-tool call counts, token savings
  versus reading files, and cache hit-rate; `squeezr stats --reset` clears.
- **Vector drift** — `squeezr vectors reconcile` compares Qdrant points and
  embedded markers against the chunk hashes in `meta.db` and reports orphans
  and stale markers. Dry run is the default; `--apply` is the destructive half —
  it deletes orphan points and resets stale embedded markers, nothing is
  written without it. `--json` renders the report machine-readable.
- **A store indexed before the secret guard still holds the old rows** — this is
  the upgrade trap, and it is not a bug in the guard. Installing a newer binary
  changes nothing in `~/.squeezr/workspaces/<storeKey>/`: the store survives a
  reinstall, so protected rows, their names in `meta.db`/`trigram.idx` and their
  paths in `queries.jsonl` are still on disk. What changes immediately is only
  the read path — those rows are never served again. The data itself leaves when
  either of these runs:
  - the **next index pass** (`squeezr index`, including a background watch
    pass): protected paths are not walked, so their rows go through the normal
    deletion cascade (symbols, chunks, refs, deferred vector reaps);
  - **`squeezr compact`** — the explicit operator route, and the only command
    that also redacts the names in the deferred-reap journal, scrubs recorded
    parameter values in `queries.jsonl` (including the repo-qualified and
    absolute forms the product itself emits), rewrites `trigram.idx` and rebuilds
    `meta.db` to give the bytes back. There is no `--purge` flag: the guard sweep
    is part of every `compact`, and nothing purges a store nobody asked to purge.

  Both report **counts only** — never paths, never values (the read-side withheld
  tally is the one naming exception, see §5). The purge line reads
  `secret guard: 7 path(s) purged, 186 row(s) removed · 1 reap journal name(s)
  redacted · 28 recorded parameter value(s) redacted · 7 point cleanup(s)
  queued`. Points the
  pass could not remove itself are queued and drained by
  `squeezr vectors reconcile --apply`. If a human asks why a secret still shows
  up in a raw store file, this is the answer: run `squeezr compact`.
