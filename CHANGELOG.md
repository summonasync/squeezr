# Changelog

All notable changes to Squeezr. Format: [Keep a Changelog](https://keepachangelog.com), versioning: [SemVer](https://semver.org).

## [Unreleased]

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
