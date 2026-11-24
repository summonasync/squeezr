# 032/T5 — freshness-guard batch cost proof

Feature: 032-async-core-profile · task T5 (order 5) · artifact 9187576e-ec06-46d0-8513-c08a3a5374b3
Date: 2026-11-24 · Harness: `tests/Squeezr.Tests/Indexing/FreshnessGuardBatchCostTests.cs`
Raw runs: `run-freshness-guard-batch-cost.log` (final), `run-repro.log` (provisional, same day)

## Claim under test

Healing N stale candidates (N = `RefreshBudget.MaxFilesReRead` = 4) through the freshness guard costs
one `IndexScoped` batch — one store open, one MSBuild solution open — not N per-file re-read cycles,
the shape the pre-T4 `ReindexFile` seam charged. Verdict: **HOLDS, in both orderings.**

## Fixture

| Property | Value |
|---|---|
| Source files | 302 `.cs` (300 pads + `Hub.cs` + `lib/Lib.cs`) |
| Projects | 2 — `Mini.App.csproj` + `lib/Mini.Lib.csproj`, joined by 1 real `ProjectReference` edge |
| Solution | `Mini.sln` at repo root (opened by RoslynSession as a solution, both projects loaded) |
| Restore | `dotnet restore Mini.sln`, proven by `obj/project.assets.json` + per-project `.nuget.g.props` |
| Session regime | MSBuild solution open proven per fixture: probe `RoslynSession.Create(repo, semantic: true)` asserts `Degraded == false` and a `"semantic mode: ..."` warning — the ad-hoc fallback would have failed the fixture before any wall was measured |
| Walk total | 305 files (302 sources + sln + 2 csproj), asserted by the full-index control |
| Isolation | workspace under `TestDirs.NewTempDir()`; run store pinned by `SqueezrTestEnvironment` (never `$HOME`); no Qdrant writes — no embedding provider is handed to any route in the harness, so the embedding phase is unreachable in all three measured paths |

## Arms

- **A — per-file (pre-T4 shape)**: seam delegate loops the batch, one full `WorkspaceIndexer.IndexFile`
  cycle per candidate — its own lock, store open, manifest read and MSBuild solution open each. This is
  the cost shape `ReindexFile` drove before T4, driven through the guard's current internal overload.
- **B — batch (T4 shape)**: the production delegate — the whole announced set into one
  `WorkspaceIndexer.IndexScoped` call.
- **Control**: `WorkspaceIndexer.Index(workspace, full: true)` — the repo-wide walk with semantic parse,
  the floor the guard saves against.
- **Order swap**: two independent fixtures, A-then-B and B-then-A. The probe session warms the MSBuild
  path before either arm, so the swap controls ambient load and cache drift, not JIT.
- Both arms run with the zero-clock stub (`() => TimeSpan.Zero`) so the wall budget cannot refuse slots
  after the first — both arms heal the same four candidates and the walls stay comparable. The budget's
  candidate capacity (4) is production's, untouched.

## Measured (final run, 2026-11-24)

Box at run time: load avg and core count ride every line below; the box carried the squeezr fleet's
own ambient load (load1 ~9-11 on 32 cores, up 0.3 days) during both arms of both orderings.

| Order | per-file (A) | batch (B) | full-index | per-file/batch | batch/full | load at report |
|---|---|---|---|---|---|---|
| A-then-B | 5.21 s | 1.02 s | 2.75 s | **5.09x** | 0.373 | 9.93 7.37 6.48, 32 cores |
| B-then-A | 3.48 s | 0.87 s | 2.02 s | **3.99x** | 0.432 | 9.93 7.37 6.48, 32 cores |

Per-arm walls with their own load lines (from the raw log):

- `one arm a (4 candidates): 5.21 s | load 10.91 7.47 6.50 on 32 cores, up 0.3 days`
- `one arm b (4 candidates): 1.02 s | load 10.91 7.47 6.50 on 32 cores, up 0.3 days`
- `one: full-index 2.75 s (305 files walked) | load 10.27 7.40 6.48 on 32 cores, up 0.3 days`
- `two arm b (4 candidates): 0.87 s | load 10.27 7.40 6.48 on 32 cores, up 0.3 days`
- `two arm a (4 candidates): 3.48 s | load 9.93 7.37 6.48 on 32 cores, up 0.3 days`
- `two: full-index 2.02 s (305 files walked) | load 9.93 7.37 6.48 on 32 cores, up 0.3 days`

Repro run (earlier the same day, `run-repro.log`): ratios 5.37x / 3.85x, batch/full 0.354 / 0.422 —
same verdict, stable across runs under different ambient load.

## Reading

- The heal of 4 stale candidates costs one batch: **0.87-1.02 s** — one MSBuild open plus four parses.
- The pre-batch route pays that open four times: **3.48-5.21 s**. The claim survives both orderings with
  the widest margin in the colder one.
- The batch is **0.37-0.43 of a full-index pass** on the same fixture (house ceiling 0.6).
- Incidental finding, not asserted in CI: at this fixture shape the per-file route (5.21 s) costs **more
  than a full index pass** (2.75 s) — four solution opens cost more than walking and re-parsing the repo.
  The batch is not merely cheaper than the old route; it is the difference between the guard being a win
  and the guard being the slowest path in the product.

## AC evidence map

1. Fixture size + project-reference count printed — the `fixture 302 cs files ...` line + the table above.
2. Both arms, both orderings, ratio + box load — the table + per-arm load lines.
3. Full-index control beside the arms — third column, measured after both arms per fixture.
4. Named smoke assertions before comparison — per arm: `Refreshed.Count == 4` then a `SearchFinds` probe
   per candidate marker (`Stale<tag><arm><index>`); plus the control asserts 305 files walked; plus the
   regime probe proves the MSBuild session. A measured zero fails before any ratio is read.
5. Advantage stated as a number — 3.99x and 5.09x per-file/batch; no issue filed against 025, the claim holds.
6. Evidence committed (this directory, force-added over the repo's `/audits/` ignore, per feature-121
   precedent); the test is an unconditional `[Fact]` in the `ProcessGlobals` collection — same shape as
   the accepted 025 cost pin — ~22 s wall per run, asserts ratios not absolutes.

## Quirk found while building the fixture

Two projects sharing one `obj/` cannot be restored sequentially: the second restore answers
"All projects are up-to-date" against the first one's fresh `project.assets.json` and the graph stays
half-restored (the app's `.nuget.g.props` never appears). Fixed by nesting the lib under `lib/` — its
obj is then both collision-free and inside the default `**/obj/**` ignore — and restoring through the
solution file, which also gives RoslynSession a solution to open deterministically.
