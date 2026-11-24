# 032/T6 — decision entry, stale-prose sweep, serial full-suite carrier

Feature: 032-async-core-profile · task T6 (order 6) · artifact `fb9b29c2-61d1-424e-9a15-e1e93ae7a82e`
Date: 2026-11-24 · Decision entry: board `e5c6091a` (type=decision, enforces, tags `decision`/`feature:032`/`enforces`) · Prose commit: `1543886`

## Carrier — the formal gate

| Property | Value |
|---|---|
| Command | `dotnet test --no-build -m:1 tests/Squeezr.Tests` — the full-suite serial rule from `tests/README.md` ("load flake evidence is only valid from a full serial suite run") |
| Result | **Failed: 0, Passed: 4858, Skipped: 0, Total: 4858** — `Passed!`, test rc=0 |
| Duration | 26 m 19 s — SUITE CARRIER START `2026-11-24T18:10:37Z` → END `18:36:58Z` |
| Head | `1543886` = T5 head `f528e76` + the prose sweep commit; no code changed under the carrier |
| `SQUEEZR_HOME` | `/var/tmp/squeezr-home-032t6-QHZk7k` — fresh `mktemp -d`, outside `$HOME`, created for this run, nothing reused |
| Box load | 32 cores; `load1` 6.77 at start (0 competing dotnet before the host spawned), 7.97–5.77 across mid-run probes, 7.70 at end; **one foreign dotnet process observed 18:19–18:29Z**, gone by 18:39Z (`probes.log`) — load produces false reds, not false greens; the run is green, so the overlap does not weaken the carrier, recorded per the 025 doctrine |
| Known-flake watch | `ServeNoIndexerNoticeTests` (parked candidate `e8014a94`) did **not** fail — zero failures total, nothing to attribute |
| Shape | 4858 = 4857 (T4's carrier, 26m18s) + 1 (T5's `FreshnessGuardBatchCostTests`) — exactly the predicted growth |

## Prose sweep — searched, matched, fixed, left

Searched (grep -rni over the repo docs): `per-file`, `per file`, `per candidate`, `IndexScoped`, `032`, `19 sites`, `async profile`, `sync-over-async`, `GetAwaiter`, `McpToolGate`, `tool gate`, `refresh budget`, `inline refresh`, plus section reads of every hit's context. Surfaces: `README.md`, `CHANGELOG.md`, `skills/squeezr/SKILL.md`, `TECHNICAL-SOLUTIONS.md`, `docs/*`.

Fixed (commit `1543886`):

1. `CHANGELOG.md` `[Unreleased]` — was empty; gained the `### Changed` 032 row: one batch per guard call (the per-file price table named and retired), cancellable gate entry (`McpToolGate.Run` deleted) and cancellable guard at the deterministic budget branch with the reached-nothing/budget shapes kept apart, the two-sided ratchet at 21, and the three residuals stated where they live — the one-batch overshoot, the lost stale-takeover stderr notice, the fail-closed state re-check on an unreadable file.
2. `README.md` `staleFiles` bullet — said "modified files refresh inline on read" with no route named, which under the pre-T4 code meant one full re-index cycle per file; now names the batch shape (one lock, one store open, up to four candidates, "never one full re-index cycle per file").
3. `README.md` `unrefreshedFiles` bullet — the "absent means" enumeration listed only nothing-stale and the store-open collapse; since T3 a token cancelled before the first slot lands in the same shape. Both collapses now named, with the cancel-after-acceptance case pointed at the budget refusal.

Left with reason:

- `CHANGELOG.md` `[1.3.0]` 017 rows ("price per file and never amortised", "a call overshoots by at most one file — CancellationToken in the core is feature 032, blocked by this one") — released-section history describing the 017-era state; byte-untouched per the tombstone/history rule.
- `CHANGELOG.md` `[1.3.0]` intro and 025 row ("IndexScoped", hook-door ratios) — the 025 hook door is unchanged by 032; its claims stay true.
- `TECHNICAL-SOLUTIONS.md` — the 11-line pointer stub ("ADR collection migrated to Overmind 2026-09-24"); zero async/ADR-002 text exists to unwrite, so the decision entry cites it as the routing stub and the file needs no edit.
- `docs/` (migration-central-store.md, migration-windows-to-linux.md, windows-checklist.md) — no guard/freshness/async prose; the only "guard" hit is the unrelated `DanglingHomePin` CI guard in the Windows checklist.
- `skills/squeezr/SKILL.md` — line 505 "reads refresh modified files inline or report them in `staleFiles`" stays true post-032; no user-visible latency or tool-shape claim in the skill moved, so per the task scope it stays untouched.

## Decision entry

`e5c6091a-d54c-4eff-b07f-b247a22ef588` states the contract 032 leaves behind per spec scope item 5: core orchestration stays sync-over-async by contract; cancellation is honored at walk/pass boundaries (`RepoFileWalker.cs:90`, `WorkspaceIndexer.cs:114`, `:588`) plus the watcher pass-budget watchdog, plus the guard's one interior point at the budget-refusal branch; the guard's residual overshoot is one batch bounded by amortization (T5 ratios 3.99–5.09× vs per-file, 0.37–0.43 of a full index), not by mid-call interruption; the ratchet pins Core at 21, two-sided. It cites the option-B cost figures measured 2026-11-13 (33 `GetAwaiter().GetResult()` sites in src vs 19 at the 2026-09-13 review, `WorkspaceIndexer.cs` 1389 lines / 43 members, 446 test call sites) against this contour's cost, states the accepted costs (takeover-notice loss on the guard path with the heal surviving via bare Acquire, `IndexLock.cs:53-75`; the fail-closed all-Stale re-check), and supersedes the never-approved ADR-002 draft in `audits/architect-review-2026-09-13.md` by reference — that file is the historical source, not rewritten.

## Ratchet re-check

`grep -rn "GetAwaiter().GetResult()" src/Squeezr.Core` on the worktree at `1543886`: exactly **21** — WorktreeReconcile 3, WorkspaceIndexer 7, EmbeddingIndexPhase 6, RoslynProvider 3, RoslynSession 2. T4's batch route reused existing sync sites and added none, so the baseline constant stays 21; the ratchet demanded no lowering. Absence of change explained: the guard's per-file loop it deleted held no `GetAwaiter().GetResult()` of its own — the sync joins it reaches through `IndexScoped` were already counted.

## McpToolGate.Run — zero surviving callers

`squeezr` MCP index is not initialized in this session (`index_status` → "Workspace is not initialized"), so the literal `find_references` call could not run; the grep equivalent is recorded instead: no `Run<T>(` definition remains in `src/Squeezr.Cli/Mcp/McpToolGate.cs` (only `RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken)`), and `grep -rn "gate\.Run(\|Gate\.Run(\|McpToolGate\.Run(" src/ tests/` returns zero hits. The five migrated tools call `RunAsync` with the SDK-bound token.
