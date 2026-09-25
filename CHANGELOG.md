# Changelog

All notable changes to Squeezr. Format: [Keep a Changelog](https://keepachangelog.com), versioning: [SemVer](https://semver.org).

## [Unreleased]

### Added (050, usage analytics)
- Telemetry client identity: every `queries.jsonl` record carries `client`/`clientVersion` — MCP records are tagged from the initialize handshake `clientInfo` (captured via a `ClientIdentity` holder wired into all six tools), CLI records are tagged `cli`, pre-existing records aggregate as `unknown` (additive schema, no file migrations)
- `squeezr stats`: new By agent (client, version, count, last seen) and By session (short id, client, count, last seen, top-3 tools) sections in the human output, plus additive `agents`/`sessions` fields in `stats --json` (035 contract preserved — existing fields byte-compatible)
- `squeezr usage`: global adoption rollup across every workspace in the registry, runnable from any directory — per-workspace blocks (storeKey, rootPath, records, by-agent counts, `(stale)` marker for vanished root paths) plus a fleet-wide by-agent totals table; filters `--agent` (case-insensitive exact), `--workspace <storeKey>`, `--since <90m|24h|7d|30d>` combine as AND; `--json` per the 035 contract; strict period parser routes all garbage into the standard error contract
- `squeezr usage --watch`: Spectre Live refresh of the rollup (default 3 s, `--interval <sec>`), clean Ctrl+C exit; `--watch` + `--json` is rejected per the 035 contract, and non-TTY runs emit a single snapshot with a stderr warning

## [1.0.2] — 2026-09-20

### Added (048/04, cross-repo documentation)
- README «Cross-repo workspaces» section: `squeezr add <path> --name <name>` for sibling repos (relative to the workspace root or absolute), one workspace = many repos, `index` covers all of them, per-repo filtering via the `repo` parameter on `repo_map`/`code_search`/`symbol_lookup`/`find_references` (`--repo` on the CLI query commands), per-repo coverage in `index_status`/`status`, and a note that discovery stays per-directory until repos are deliberately shared
- The MCP instructions block's cross-repo line now carries the exact configuration command (`squeezr add <path> --name <repo>`) instead of a hint; SKILL.md section 3 expanded to match

## [1.0.1] — 2026-09-20

Dogfood hardening wave (2026-09-20 rollout): GPU-first embeddings, agent-experience overhaul, MCP registration fixes. Full suite 910 → 956.

### Added (047, embedding provider auto)
- `embeddings.provider: "auto"` — the default for new workspaces: per-run resolution prefers ollama (GPU) when the endpoint answers a 2s `GET /api/tags` probe, falls back to onnx (CPU) with a logged reason; no mid-run switching (failures keep the existing error + resume path)
- One log line per run (`embeddings provider: ollama` / `embeddings provider: onnx (ollama unreachable: <reason>)`, stderr in all modes so `--json`/`--events` stdout stays clean); `index --events` `started`/`completed` carry the resolved `provider` (additive, skip-null); `status --json` gains `embeddingsProvider`; TUI accepts `auto`
- Stable `workspace_registry_busy` error code (registry busy → terminal `error` without `started`, same code in `--json` envelopes); `--reembed --no-semantic` warns instead of silently doing nothing; probe skipped for effective no-semantic runs
- Measured on RX 7900 XTX (ollama/Vulkan): 12465 chunks in 6.9 min at ~170% CPU vs 30.3 min on onnx int8 at ~1400-1600% CPU

### Added (048, agent experience)
- Instructions block rewritten from slogans to a scenario matrix (repo_map for unfamiliar repos; symbol_lookup for exact symbols; find_references before edits; code_search hybrid; expand_span for spans; exact strings / unindexed repos → grep is correct; cross-repo via `squeezr add` + index_status guidance) and inserted after the first heading on fresh installs; existing marker positions replaced in place
- Six MCP tool descriptions rewritten scenario-first, hedges removed and outlawed by a pin test; SKILL.md documents the qwen deferred-tools (`tool_search select`) behavior
- `squeezr hook pre-read`: advisory PreToolUse hook payload producer for Claude Code — stdin hook JSON → `additionalContext` nudge when a Read targets a large file (≥256 KB) or wide range (limit ≥400) inside a registered workspace; silent no-op otherwise (fail-open, exit 0, no network); wiring snippet in README/SKILL, applied by hand to `~/.claude/settings.json`

### Fixed (045 wave)
- qwen MCP registration: `qwen mcp add` no longer passes the `--` separator (its yargs fork counts positionals differently) and passes `--trust` before the name — registration succeeds and the server is trusted (no per-call confirmations); claude/kimi/codex command shapes unchanged
- Default ignore globs now exclude Claude Code worktrees (`**/.claude/worktrees/**`) — a repo with agent worktrees indexed full copies of itself (Mindlash dogfood: 11 124 extra files, ~5x chunk inflation)
- MCP config entries written by squeezr no longer pin `cwd` — the stdio server inherits the session's working directory and resolves the workspace itself (a pinned cwd made kimi serve the Squeezr repo index in every project); `SupportsCurrentDirectory` removed with the emission

### Added (045, install from release + agent onboarding)
- `scripts/install-release.sh`: one-command installer for machines without a Squeezr checkout — `curl -fsSL <raw-url> | bash` resolves the latest Gitea release via its API (or `--version <tag>` to pin/roll back), downloads the linux-x64 tar.gz, checks archive integrity, unpacks into `~/apps/squeezr/<tag>/` and atomically repoints the `~/.local/bin/squeezr` symlink; idempotent (re-run refreshes the symlink only, `--force` re-downloads), no sudo, env overrides `SQUEEZR_GITEA`/`SQUEEZR_INSTALL_DIR`/`SQUEEZR_BIN_DIR`; arm64/Windows get an explicit refusal with a manual hint
- `squeezr mcp install --all --scope user --yes`: fully non-interactive registration — new `--all` flag (every detected client, alongside the existing `--client`), `--yes` skips all prompts (`--yes` without `--scope` defaults to user and prints the decision); the flagless interactive flow is unchanged
- `skills/squeezr/SKILL.md`: agent onboarding skill — install-from-release ritual, services, workspace init/index, MCP registration, six-tool cheat-sheet with token budgets, troubleshooting (gRPC 6334 endpoint, dimension mismatch → `index --reembed`, fleet); installs only on an explicit human request; distributed as the Overmind kit «squeezr» (manual import from `skills/squeezr/`, phase 1)
- README «For AI agents» section: the one-conversation setup ritual with a link to the skill

## [1.0.0] — 2026-09-18

First stable release. v1 MVP (M1–M6) plus the post-MVP feature set: central workspace store with managed services, watch fleet, MCP client onboarding, machine-readable CLI with progress events, token stats, live-validated semantic search (e2e on a real repo, Linux), cross-platform install and CI.

### Fixed
- Default ignore globs now exclude Next.js build output (`**/.next/**`) — before, a Next.js repo indexed its `.next` bundles, and minified vendor chunks dominated semantic search results (e2e-028 finding F1; existing workspaces can add the glob to `indexing.ignore` themselves)

### Added
- `squeezr mcp install/remove/doctor`: one-command onboarding of the MCP server into agent CLIs — Claude Code, Kimi Code, Qwen Code, Codex CLI (`--client claude|kimi|qwen|codex|all`, `--scope user|project`, `--no-watch`, `--no-instructions`, `--yes`, `--json`; feature 034)
- Onboarding extras: `serve --watch` entry by default, idempotent marker-block agent instructions in the client's instruction file (Claude — CLAUDE.md, Kimi/Codex — AGENTS.md, Qwen — QWEN.md), `CLAUDE_PROJECT_DIR` fallback in `serve` workspace-root resolution (Claude Code has no cwd config key)
- MCP protocol smoke tests: initialize → tools/list → tools/call across all six tools

### Fixed
- MCP `tools/list` now reports tool descriptions (agent-facing hints were empty)

### Added (035, machine-readable CLI)
- `status --json`: single camelCase object with top-level `files/symbols/chunks/staleFiles/indexAge` plus per-repo breakdown, field names aligned with the MCP envelope
- `search --json` is now NDJSON: per-line `{"type":"match"}` entries flushed per line + final `{"type":"summary"}` (`matches/tokensUsed/maxTokens?/truncated`)
- `squeezr workspace list`: workspace composition from the manifest; `--json` — NDJSON, one repo per line
- Uniform error contract for all `--json` commands: `{"error":{"code","message"}}` on stderr + exit code 1, stable codes via `WorkspaceException.Code`
- Stale-file detection in JSON mode (`StaleFileDetector`: walk + SHA-256 vs metadb, semantics = next incremental index pass)
- Human-readable output frozen as regression guard (contract tests)

### Added (036, progress events)
- `index --events` and `serve --watch --events`: NDJSON progress event stream, one event per line, flushed per line — `started` → `fileIndexed` (cumulative file count) → `progress` (phase totals for semantic/embeddings/trigram) → `completed` (totals, durationMs, inserted symbols) / `error` (terminal, stable codes)
- Event transport: `index` streams events on stdout; `serve --watch` streams them on stderr so the MCP JSON-RPC channel on stdout stays untouched
- Composes with `--json`: events form a complete sequence first, then the final `--json` result object; errors emit a terminal `error` event plus the uniform `{"error":{"code","message"}}` envelope on stderr with exit code 1
- A quiet incremental `index --events` run emits exactly `started` → `fileIndexed`×N → `completed`; human-readable output and watch/MCP behavior are unchanged

### Changed (041, default embedding model)
- Default local ONNX embeddings: nomic-embed-text-v1.5 (official int8 ONNX from nomic-ai, ~131 MB, 768d, WordPiece tokenizer, mean pooling + L2 normalization) instead of bge-code-v1 (1.7 GB Q4, 1536d) — an order of magnitude lighter and faster on CPU; bge-code-v1 remains a supported option (`scripts/fetch-model.sh --model bge-code-v1` / `fetch-model.ps1 -Model`, `"model": "bge-code-v1"` in `embeddings.onnx` of `.squeezr/config.json`)

### Added (041, embedding model profiles)
- Embedding model profiles (`EmbeddingModelProfiles`): known models resolve dimensions, pooling, tokenizer kind and per-usage prefixes from one place instead of ad-hoc config values; squeezr applies the nomic `search_document:` / `search_query:` prefixes itself, so indexed text and queries stay prefixed consistently
- DF-12 dimension guard: switching the embedding model on an existing workspace fails fast with an explicit `dimension_mismatch` error ("Qdrant collection ... has vector size X ... produces Y ... run 'squeezr index --reembed'") instead of silently upserting wrong-dimension vectors

### Fixed
- Post-install hint in `scripts/install.sh` no longer hardcodes the legacy model name "bge-code-v1" (review-040-01 W4); it now points at the default model from `fetch-model.sh`

### Changed (038, TUI onboarding)
- TUI main menu is now contextual and action-named: a folder without a workspace (no `squeezr.workspace.json`) gets a two-item menu (Initialize workspace / Exit), an initialized workspace gets the full cycle — Dashboard / Search / Map / Index / Add repository / Config / Drop workspace / Exit; the «Wizards» submenu is gone, init/add/drop are top-level items

### Added (038, TUI onboarding)
- Init offer when entering TUI in a folder without a workspace: «No workspace found here. Initialize now?» (default yes) → init form; declining keeps the two-item menu open (no restart needed)
- Zero-repos offer on the dashboard: «No repositories yet. Add one now?» (default yes) → add-repository form
- Dashboard services status: when Qdrant is down, the status line is followed by an explicit hint — `qdrant is down — start it with: docker compose up -d`

### Added (039, central workspace store + managed services)
- Central workspace store `~/.squeezr/` (override env `SQUEEZR_HOME`): registry `workspaces.json` (`name/rootPath/storeKey/createdAt/lastUsedAt`), per-workspace store dirs `workspaces/<storeKey>/` (manifest `workspace.json`, `config.json`, `meta.db`, `trigram.idx`, `index.lock`), `models/` for ONNX models, `infra/` for the managed compose file
- `squeezr init` leaves zero files in the project — no `.squeezr/`, no in-project manifest, no compose
- Git-like workspace discovery: every command resolves the workspace upward from the current directory via the registry (any subdirectory works); legacy in-project workspaces (`.squeezr/` + `squeezr.workspace.json`) migrate to the central store automatically on first access (idempotent, stderr notice)
- `squeezr services up|down|status [--json]`: managed Qdrant + Ollama stack from `~/.squeezr/infra/docker-compose.yml` (regenerated on every `up`, marker `# managed by squeezr`); ROCm device passthrough (`/dev/kfd`, `/dev/dri`) for AMD GPUs; `down` stops containers but keeps data volumes; works outside any workspace
- Ollama embedding provider (`embeddings.provider: "ollama"`): native `/api/embed`, default model `nomic-embed-text` (768d) with the `nomic-embed-text-v1.5` profile (prefix-symmetric with the ONNX provider), no API key, automatic model pull on index preflight, explicit error pointing at `squeezr services up` when Ollama is unreachable
- `squeezr drop --store-key <KEY>`: removes a stale registry entry and its store dir when the workspace root no longer exists
- DF-11: embedding upsert + chunk marking now commit per batch (32) instead of per 4096-chunk window — a crash mid-run loses at most one batch, and the next `index` run resumes with the remaining pending chunks; progress is reported cumulatively (`embedded/total`)
- TUI dashboard shows both services (Qdrant points/pending, Ollama availability + model installed) with `squeezr services up` hints

### Changed (039, central workspace store + managed services)
- **Breaking:** `workspace list` now reads the central registry instead of the current workspace's manifest; `--json` entries carry `name/rootPath/storeKey/createdAt/lastUsedAt/stale` — the repo-level `path` field is replaced by `rootPath` (accepted without compat alias, review-039-03 W1; pre-release 0.9.0)
- **Breaking:** `search` (and other workspace commands) outside any workspace now fail with error code `workspace_not_initialized` (was `index_not_built`); `index_not_built` is kept for "inside a workspace without an index" (review-039-03 W2)
- `drop` removes the workspace's store dir + registry entry + Qdrant collection; the post-drop hint now points at `squeezr services down` (shared volumes are never removed by `down`)
- Dead `state.json` format removed: init no longer creates it, migration deletes it (live state remains LiteDB `index_state`)
- `scripts/fetch-model.sh`/`fetch-model.ps1` default download target is now `~/.squeezr/models` (spec 039 D11; pass a directory to bundle with the CLI instead); `install.sh` model hint checks both the central store and the bundled copy

### Fixed (039, central workspace store + managed services)
- Workspace registry read-modify-write is serialized cross-process (`workspaces.lock`), so concurrent CLI runs can't lose each other's entries

### Changed (042, dogfood wave 2)
- `squeezr services up` shows stage progress (`Pulling images (first run may download ~3.6 GB)…`, `Starting containers…`) instead of sitting silent during the first image pull; raw docker output is available under `--verbose` (stderr; `--json` stdout stays clean)
- Repeat `up` calls pull only missing images (`docker compose pull --policy missing` on compose v2.24+; plain pull with a stderr note on v2.21–2.23) — offline restarts with cached images work again
- The pull phase of `services up` no longer has any timeout (previously: an idle watchdog killing after 5 minutes of total output silence plus a 60-minute wall-clock cap) — a stalled or unwanted download is interrupted with Ctrl+C, which kills the compose process cleanly; pull failure errors still carry the manual resume command (`docker compose -f ~/.squeezr/infra/docker-compose.yml -p squeezr pull`)
- README: managed-services section now documents first-run realities (image sizes, resume, compose version requirements)

### Added (043, watch fleet + daemon heartbeats)
- Running watch daemons (`squeezr watch`, `serve --watch`) now write a heartbeat file into the workspace store dir — `<storeDir>/<kind>.heartbeat`, kind = `watch` / `serve-watch`: `{pid, kind, startedAt, lastBeatAt}`, refreshed every 5 s and after each successful catch-up pass, atomically, removed on clean exit
- `squeezr fleet [--json]`: registry-wide daemon overview — per workspace, both daemon kinds (alive: pid + uptime; dead: marker kept and shown, never cleaned), pending chunks and last full index from meta.db (best-effort, `n/a` when unavailable), stale rootPath warnings on stderr; works from any CWD, no workspace required
- `squeezr fleet stop <storeKey>`: stops both daemon kinds (SIGTERM → 5 s grace → SIGKILL on Unix, process-tree kill on Windows); heartbeat pids are verified before signaling — process must look like squeezr (name/cmdline match + start time vs heartbeat `startedAt`), refusing to touch foreign or reused pids; no alive daemons → `not-running`, exit 0
- `squeezr fleet restart <storeKey>`: restarts the CLI `watch` daemon only — stops it if alive, then spawns a detached `squeezr watch` with cwd = workspace root; a live `serve --watch` errors with the MCP hint (the MCP client owns it); stop/restart accept `--json` (NDJSON result, clean stdout per the 035 contract)

### Added (021, token stats + query telemetry)
- Query telemetry: every navigation command (CLI `search`/`symbol`/`refs`/`map`/`expand` and all six MCP tools) appends one line to `queries.jsonl` in the workspace store dir — `ts`, `sessionId`, `tool`, `params`, `tokensUsed`, `truncated`, `hits`, `durationMs`, `cacheHit`, `indexAgeSeconds`; a CLI process is one session, an MCP connection gets its own; append failures are swallowed (telemetry must never break a query), unreadable lines are skipped on read; failed commands record nothing
- `squeezr stats [--json] [--reset]`: per-tool table (count, avg tokens, truncation rate), counterfactual savings — actual `tokensUsed` vs reading every expanded file whole (each file counted once, size-based estimate consistent with the chunker, files missing from the index reported separately), serve-cache hit-rate across sessions, covered period; `--json` emits a single camelCase object (035 contract), `--reset` truncates telemetry (`{"reset":true,"recordsRemoved":N}` in JSON mode, human line `Cleared N telemetry record(s).`); retention hint past 50k records, no auto-rotation by design
- MCP response caching: `repo_map`, `code_search`, `symbol_lookup`, `find_references` cache responses per serve session — LRU 100, canonical-JSON key (recursive property sorting, null-vs-absent equivalence); the cache generation is derived from meta.db mtime, so any reindex (watch pass or external `squeezr index`) invalidates all keys; hits return the stored envelope unchanged and are marked `cacheHit: true` in telemetry; `expand_span` (live file reads) and `index_status` are not cached

### Removed (029, dead config)
- `gitAware` config key and its TUI editor field removed — the key existed since M2 but was never read by any code path (indexing is always a full SHA-256 diff walk, freshness is handled by the read-time guard and watch mode); the dead `lastIndexedCommit` LiteDB field in `RepoDocument` is gone with it; existing configs that still carry `"gitAware": ...` keep loading (unknown keys are skipped, covered by a regression test)

### Changed (030, install hardening)
- `scripts/install.ps1` accepts `-Rid win-x64|win-arm64` (default win-x64) and wipes `artifacts\publish` before publishing, so switching RIDs can't leak binaries from a previous publish into the install dir (parity with install.sh); post-install embedding-model check now matches install.sh — warns to run `fetch-model.ps1` when no model is found in the central store or the install dir

### Added (031, CI publish smoke)
- CI `publish-smoke` job: single-file self-contained publish for linux-x64 and win-x64 on every push to main and on PRs, then a smoke run of the published binary (`squeezr --version`) on the matching runner — publish regressions surface on regular pushes instead of during a release cut; `cache: true` dropped from both CI jobs (setup-dotnet cache needs `packages.lock.json`, which the repo does not use — review-029-030-031-01 W1)

## [0.9.0-pre] — 2026-09-13

Pre-release for dogfooding. v1 MVP (M1–M6) closed, post-MVP features landed.

### Added
- Hybrid code index: trigram engine (delta+varint postings, tombstone segments, mmap), Roslyn symbols/refs for C#, tree-sitter for JS/TS/TSX, fallback chunker
- CLI: `init`, `add`, `index`, `search` (smart-case, `--regex`), `symbol`, `refs`, `map`, `expand`, `status`, `compact`, `drop`, `watch`, `tui`
- MCP server (`serve`, stdio) with six tools: `repo_map`, `code_search`, `symbol_lookup`, `find_references`, `expand_span`, `index_status`; uniform envelope (`tokensUsed`, `truncated`, `warnings`, `indexAge`, `staleFiles`)
- Watch mode: `squeezr watch` and `squeezr serve --watch` — debounced catch-up passes with IndexLock backoff (feature 020)
- Embeddings: local ONNX (bge-code-v1) by default, OpenAI-compatible provider; graceful degradation to exact-only search when Qdrant is down
- Linux support: bash install/uninstall/fetch-model scripts, CI on ubuntu + windows (feature 026)
- `--version` flag; MCP `ServerInfo.Version` reports the real assembly version

### Fixed
- `OperationCanceledException` no longer swallowed by parse catch-filters (RoslynProvider, TreeSitterProvider) — prep for async core profile (032)
