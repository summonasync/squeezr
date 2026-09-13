# Changelog

All notable changes to Squeezr. Format: [Keep a Changelog](https://keepachangelog.com), versioning: [SemVer](https://semver.org).

## [Unreleased]

### Added
- `squeezr mcp install/remove/doctor`: one-command onboarding of the MCP server into agent CLIs — Claude Code, Kimi Code, Qwen Code, Codex CLI (`--client claude|kimi|qwen|codex|all`, `--scope user|project`, `--no-watch`, `--no-instructions`, `--yes`, `--json`; feature 034)
- Onboarding extras: `serve --watch` entry by default, idempotent marker-block agent instructions in the client's instruction file (Claude — CLAUDE.md, Kimi/Codex — AGENTS.md, Qwen — QWEN.md), `CLAUDE_PROJECT_DIR` fallback in `serve` workspace-root resolution (Claude Code has no cwd config key)
- MCP protocol smoke tests: initialize → tools/list → tools/call across all six tools

### Fixed
- MCP `tools/list` now reports tool descriptions (agent-facing hints were empty)

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
