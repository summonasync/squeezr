# sqzrlite benchmark baseline

Spec 130 scope item 5: startup time, steady-state RSS, full-index wall on a fixed fixture,
per-query p50/p95 — recorded in the repo, re-runnable, so "lite is lean" stays a number.

**Re-run everything in this file with `scripts/bench-sqzrlite.sh`** (published binary +
build-from-source startup variants, the full-product index comparison row, both idle
sampling windows, the 32-query mix, the check-price A/B, and two edit bursts with collision
queries). The script prints the same tables this file carries; on a busier box the numbers
move, the shape does not. No absolute-time assertion lives here — this is a baseline, not a
test. This run's verbatim script output: `audits/130-t6-2026-11-27/run.log`.

## Methodology

- **Run**: 2026-11-27 17:51–18:00 UTC, commit `65e7d09` (`feat/130-sqzrlite`), binary stamp
  `0.1.0+65e7d09`.
- **Box**: Linux 7.0.0-44-generic, 32 cores, 61 GB RAM, dotnet SDK 10.0.301. Load 1m stayed
  0.40–0.63 across the run; every phase line of the script prints its own load reading, and
  the samples below are from those lines.
- **Home**: `SQUEEZR_HOME` pinned to a fresh `mktemp` dir outside `$HOME` for the whole run,
  removed on exit. The live `~/.squeezr` is never opened.
- **Fixture**: generated fresh by the script, not copied — 150 files (120 `.cs` + 30 `.md`),
  70,217 B, six-entity rotation (OrderService / PaymentGateway / LedgerEntry / Reconciler /
  VaultClient / RateLimiter by `i % 6`). One fixture feeds every number below, including the
  full-product comparison row.
- **Measured vs inferred**: every number below is a direct read of that run except the
  per-candidate guard price, which is derived by subtraction and labeled so.
- **Sampling**: one-shot commands via `/usr/bin/time -v` (wall + max RSS); the serve session
  via `/proc/<pid>/statm` + `stat` at 2 s. CPU % is the fraction of ONE core (`utime+stime`
  over wall); it can exceed 100 % on multi-core work — lite never gets near that idle.
- **No services**: Qdrant/Ollama were untouched; lite answers exact arms only. The full
  product's semantic-phase numbers are cited from the research doc, not re-measured here.
- **No response cache**: lite omits `ToolResponseCache` by design (T3), so every mix number
  is the true cold path — an identical query never replays a cached answer.

## Binary and assets (measured)

| Fact | Value |
|---|---|
| Single-file binary | 218,339,864 B (~208 MB), self-contained + ReadyToRun, compression off |
| Publish dir | 31 entries: 1 binary + 28 native tree-sitter `.so` + 2 MSBuild BuildHost trees, 0 loose managed DLLs |
| Build output | 44 DLLs (framework-dependent build side) |
| Trim / NativeAOT | Both rejected by measurement, not opinion: Spectre.Console.Cli's reflection-based command settings die (`CommandRuntimeException`, exit 134 on every invocation incl. `--version`; IL3050/IL2104 at build). Pinned in `scripts/publish-sqzrlite.sh`; proof in `audits/130-t5-2026-11-27/` |

## Startup (measured, 5 reps each)

| Path | published single-file | build-from-source (`dotnet sqzrlite.dll`, Release) |
|---|---|---|
| One-shot `map --depth 2` cold, median wall | **0.19 s** (walls 0.19–0.20) | **0.30 s** (walls 0.29–0.31) |
| One-shot `map` peak RSS, avg | 100 MB | 75 MB |
| `serve` spawn → `initialize` answered | 125–141 ms | — |
| `serve` spawn → first `repo_map` answer, median | **0.237 s** (228–244 ms) | **0.467 s** (460–479 ms) |

Watcher arming adds nothing to the first-answer path: the watch engine starts after the
server is up (T4), and the idle session below still answers its first query at the same
latency class.

Cited reference points: T5 measured 0.17–0.19 s map / 0.263–0.267 s serve-to-first-answer on
the published binary at `7227391` — the same class as this run's numbers at `65e7d09`. The
research doc measured the full product at ~0.37 s one-shot `map` cold and 263–287 ms serve
spawn→init: the published lite binary starts ~2x under the full product on both doors.

## Full index on the fixture (measured)

| Subject | Wall | Max RSS | Store after |
|---|---|---|---|
| lite `index` (published binary) | **0.78 s** | **209.5 MB** (214,500 KB) | 150 files, 960 symbols, 990 chunks (960 code + 30 docs) |
| full product `index --no-semantic`, same fixture, same protocol | 1.37 s | 202.8 MB (207,628 KB) | same store shape (shared Core engine) |

The full-product row carries `dotnet` host startup inside its wall; the delta against lite
is host/packaging, not indexing machinery — both arms are the same exact-only Core engine.
The expensive arm is the semantic phase, which lite deletes entirely: on the research doc's
1165-file fixture it measured **18.09 s / 360.7 MB exact-only vs 598 s / 1.11 GB with
semantic** — 33x wall, 3x memory. Cited, not re-measured (that arm needs Qdrant/Ollama).

## Steady idle, watcher armed (measured)

| Variant | Window | RSS min/avg/max | CPU |
|---|---|---|---|
| published binary, `serve --watch`, converged store, no queries | 240 s | **93.5 / 98.0 / 102.5 MB** | **0.27 %** |
| build-from-source Release, same protocol, same fixture, same run | 240 s | **65.9 / 69.3 / 72.8 MB** | **0.29 %** |

Reads:

- Idle CPU matches the whole measurement history: 0.27–0.29 % here, T4's 0.25 %, research
  proxy 0.22 % — the armed watcher is free when the store is converged.
- The build-from-source idle lands dead on the prior datapoints: 69.3 MB avg here vs T4's
  69.4 MB avg and the research proxy's 71.8 MB. The published single-file idles ~29 MB above
  it — the self-contained bundle's private runtime pages, packaging not engine. Both sit
  max 102.5 MB, an order under anything with an embed drain (research: 1.2–1.4 GB at
  1200–1800 % CPU while draining).

## Query mix on the converged store, guard on (measured)

32 scripted queries, sequential, one initialized stdio session, post-index, pre-burst:

| Tool | n | p50 | p95 |
|---|---|---|---|
| `code_search` (identifier / regex / prefix / kind mix) | 12 | 64 ms | 106 ms |
| `symbol_lookup` | 6 | 12 ms | 14 ms |
| `find_references` | 6 | 13 ms | 14 ms |
| `repo_map` (depth 1–3, prefix) | 6 | 14 ms | 14 ms |
| `index_status` | 2 | 19 ms | 14 ms |
| overall | 32 | **14 ms** | **104 ms** |

Envelope stats over the mix: content check covered 0–50 candidates per query, the structure
check covered all 150 rows on `repo_map`, `staleTotal` sum **0** — the store is converged
and every verdict says so.

## Freshness-check price (measured walls, inferred per-candidate)

| Arm | median wall | median candidates checked |
|---|---|---|
| `code_search` topK=2, single-candidate term | 11 ms | 1 |
| `code_search` topK=50, wide term | 162 ms | 50 |

**~3.1 ms per candidate [inferred by subtraction]** — and the label matters: the delta
carries the guard's row lookup + stat + SHA-256 check AND the answer assembly for 49 extra
rows, so it is an upper bound on the pure check. The research doc's historical check price
was 0.37–1.5 ms/candidate (`RefreshBudget.cs` history); the check staying inside the
round-trip noise at 50 candidates is the claim that matters, and it holds.

## Watcher pass after an edit burst (measured)

Two bursts against the live `serve --watch`, each followed by collision queries fired into
the debounce/pass window (product-reported pass lines, verbatim):

| Burst | Edited | Pass line | Pass wall | Queries during the window | Inline heals | Stale reports |
|---|---|---|---|---|---|---|
| burst10 (T4-comparable shape) | 10 | `watch pass +0 ~7 -0 (skipped: 0, 105ms)` | 105 ms | 10, walls 59–470 ms | 3 | 0 |
| burst30 | 30 | `watch pass +0 ~17 -0 (skipped: 0, 16ms)` | 16 ms | 20, walls 54–147 ms | 13 | 0 |

- The pass wall is the product's own report; edit→completion measured 2.48 s / 2.17 s
  (debounce 2 s + detection), bounded by the 0.5 s query cadence.
- The `~7`/`~17` vs edited `10`/`30` split is the race working as designed: the query-side
  guard healed 3 + 13 burst files inline before the watcher's pass snapshotted them. Healing
  happens exactly once per file, whichever arm wins.
- Post-burst queries: 79 / 7 / 7 ms, `staleTotal` sum 0 — the store converges with no
  leftover debt.
- Scale context: these passes are milliseconds because the fixture is 150 tiny files. On
  T4's ~430-file real-repo fixture a 10-file burst passed in **25.8 s** — pass wall scales
  with store content, and the honest number for real repos is T4's, cited.

## Research doc §4 re-measures, answered

1. **Lite full-index wall/RSS vs the full product's numbers — measured.** 0.78 s / 209.5 MB
   on the 150-file fixture; same-fixture full-product exact arm 1.37 s / 202.8 MB (host
   startup included); the research expectation "≈ the `--no-semantic` row" held — the exact
   arm is shared machinery, the semantic arm (598 s / 1.11 GB on 1165 files, cited) is what
   lite deletes.
2. **Watcher pass wall on lite — measured.** 105 ms for a 10-file burst on the synthetic
   fixture (16 ms for 30 files); 25.8 s for 10 files of real product code on T4's ~430-file
   fixture. The research expectation "≈ hook/IndexScoped prices, not the 90–140 s semantic
   figure" held — passes heal exact-only, no Roslyn session per file.
3. **Idle RSS with watcher on — measured.** 93.5/98.0/102.5 MB / 0.27 % CPU (published,
   240 s); 65.9/69.3/72.8 MB / 0.29 % (build-from-source, same run). The research expected
   "below the 71.8 MB proxy — no Roslyn/MSBuild assemblies loaded": the framework-dependent
   build lands there (69.3 MB avg ≈ proxy 71.8 MB and T4's 69.4 MB); the published
   single-file idles ~29 MB above it on packaging, max 102.5 MB, CPU identical.
4. **Guard price per query — measured walls, inferred per-candidate.** ~3.1 ms/candidate
   upper bound at 50 candidates (subtraction), single-candidate round-trip 11 ms, historical
   check price 0.37–1.5 ms/candidate cited; the guard is noise inside every query's budget.
5. **Lock-contention branch during a live pass — measured behavior, branch not triggered.**
   30 collision attempts across two bursts: 16 heals won the race inline (3 + 13), every
   during-pass query answered (54–470 ms), zero stale reports, zero post-pass debt. The
   branch (a heal colliding with the pass's `index.lock` → `WorkspaceLockedException` →
   honest stale report, `FreshnessGuard.IsRecoverable` catch) requires the lock to be held
   past the heal's bounded wait — lite passes hold it 16–105 ms here, so the wait always
   wins. T4 measured the branch firing honestly once on the real-repo fixture (25.8 s pass,
   `stale=1` with the file named in `staleFiles`): on lite-sized stores collisions resolve
   by waiting, on real-repo sizes by a named stale report — both verified.
6. **Candidate C (watchd) on the lite binary — decided-against, not re-measured.** Spec 130
   resolved the freshness question from the research numbers ("Q1 resolved from the
   dispatched research numbers (model A + verdict)", spec log, validated 2026-11-24 with
   human proof): lite ships model A — serve-inline watcher + query-time verdict. Watchd is
   the model C surface that decision rejected (two process shapes, ≈125 MB combined idle on
   the full build, autostart ceremony — research §2C/§3.4), and the spec's anti-scope sends
   "full watchd parity" to a separate later feature. Re-measuring a rejected shape on the
   lite binary is deferred until that feature exists.

## Cited numbers (measured elsewhere, labeled)

| Source | Number | Why cited, not re-measured |
|---|---|---|
| Research §3.2 (2026-11-24) | full product index exact-only 18.09 s / 360.7 MB vs semantic 598 s / 1.11 GB, 1165-file fixture | the semantic arm needs Qdrant/Ollama; fixture differs from this doc's |
| Research §3.3/§3.4 | embed-free serve proxy idle 69.3/71.8/75.0 MB / 0.22 %; watchd two-process ≈125 MB | pre-lite proxy; the lite rows above supersede them for lite |
| Research §3.5/§3.6, `RefreshBudget.cs` history | check 0.37–1.5 ms/candidate; C# heal 2.7–7.6 s/file under the old full-product machinery | historical guard context for §4-4 |
| T4 task log (2026-11-27) | idle 64.4/69.4/72.3 MB / 0.25 % over 200 s (build-from-source, real-repo store); burst pass 25.8 s / 10 files on ~430 files; contention `stale=1` fired once; during-pass queries 155–215 ms | real-repo datapoints this doc's synthetic fixture cannot produce |
| T5 task log + `audits/130-t5-2026-11-27/` | map cold 0.17–0.19 s / serve-to-first-answer 0.263–0.267 s on the published binary; AOT/trim runtime-death proof; publish shape | same packaging, one commit earlier — re-measured at `65e7d09` above |
