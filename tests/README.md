# Squeezr.Tests — discipline notes

Patterns below are load-flake lessons from full-suite serial runs (features 087, 092, 100). A new test that touches a live engine, a spawned process, or wall-clock ages should follow them before it joins the suite.

## Patterns

### 1. Quiescence comes from the machinery's own tail record, not from a DB counter

`WorkspaceWatcherEmbedDrainTests.SaturatedBudget_CatchUpReArms_EmptyBatchesUntilPendingZero` flaked as `drains.Sum(...) should be 18 but was 15` (review-070-05, BUGS waves 097/098): the drain marks chunk rows embedded mid-pass, so a `CountPendingChunks == 0` poll can return while the final pass has not reported `PassCompleted` yet — the recorder snapshot then misses the last drain record. A counter that flips before the record lands is not a completion signal.

Rule: wait for the terminal state the machinery itself produces (the debounced empty pass that re-arms nothing), then snapshot and assert. If a fixture's shape makes that terminal record conditional, say so in the fixture asserts.

### 2. Never exec a path that was just open for writing — install via temp + rename

`ClientCliBridgeTests` hit Linux `ETXTBSY` on the fake `claude` in full runs (092): execve refuses a file any handle still holds for writing, and a fixture that writes the script straight onto its final name races the spawner for that inode. `FakeCli.Install` writes to `<name>.installing`, sets the exec mode on the temp inode, then `File.Move` — the final path is an inode nobody ever opened for write.

Rule: any test fixture that writes-then-spawns an executable (scripts, fake CLIs) goes through an atomic rename; mode changes belong on the temp file so they travel with the inode.

### 3. Floor-quantized ages get an invariant bound, never an exact tick

`FreshnessPresenceTests.CodeSearch_LiveWriterOnStore_NamesPidAndBeatAge` asserted `watch beat 0s old`; the age is wall-clock between fixture setup and the tool call, floor-quantized to seconds, so any load that stretches the harness past 1s flipped it to `1s` (092). The producer only prints the line when its probe deems the writer alive — the age is guaranteed below `DaemonHeartbeat.StaleThreshold`.

Rule: assert the invariant the producer guarantees (parse the value, bound it by the documented threshold), plus the race-free parts (pid, line presence, format). Exact quantized values ("0s", "5s") are flake bait.

### 4. Waits crossing an edit-propagation chain get a measured multiple; a stop wait sits outside the machinery's own join window

The 100 fix covered the drain family's saturated member, but the class's other members kept a shared 30s budget over a chain of FSW delivery + one debounce window + a full pass. That chain idles in low single-digit seconds (1-6s whole-test isolated at load ~31 during 104), yet the same shape killed sibling classes on their 60s budgets the same day — `ServeWatchClaimTests.TwoParticipants_OnOneStore_OwnerIndexes_ObserverAnswers` at load 44 (104's repro run 1) and `ServeEmbedDrainTests.NoEmbedWatch_PassIndexesEditsButPendingStays` at load 109 (104's gate 3, first attempt) — while the one class on a three-minute budget passed under identical conditions. Name the multiple at the assert site: 104's budget is roughly 30-90x the idle chain, the same size 100 picked for the seven-window chain. The cost is stated too: a machinery regression that used to die at 30s now hides until the budget expires — a wait failure at this size means a real stall, and the suite pays that stall in full. Related margin rule: a test-side stop wait must exceed the machinery's internal join window (`WorkspaceWatcher.PassJoinTimeout` = 5s), never match it — matching gives a loaded box zero margin and fails the test on a stop that was still working.

Rule: size wait budgets from measured chain latency under load, state the multiple and the blind-window cost next to the budget; a test that joins or stops machinery with its own timeout must budget strictly more than the machinery's internal join.

### 5. The suite leaks /tmp workspaces; on a tmpfs /tmp that kills the whole box, not one run

Fixtures built workspaces under `/tmp/squeezr-tests/<guid>` and nothing removed them until 079's sweep landed (per-run registration, teardown on exit, stale-leaf sweep); the lesson stands for any fixture root that skips that registration. `/tmp` on this box is a tmpfs, so its inode table is finite just like its space: 106's first serial gate died 501/3397 in an ENOSPC cascade the moment two concurrent suite runs pushed the cumulative day-long leak past the 1048576-inode ceiling — and the blast radius was not the suite but the box: every agent shell on it keeps per-call scratch in /tmp, so tooling itself went down with the mount.

Rule: a run failing at scale (hundreds at once, not one knife) is a box symptom first — check `df -i /tmp` before reading stack traces; and no wait-budget widening can paper over resource hygiene, because knives keep tests honest only while the machine underneath them is alive.

### 6. A full-budget expiry on a just-booted box is a boot-storm transient until reproduced warm

106's first post-reboot serial run expired `ServeEmbedDrainTests.NoEmbedWatch_PassIndexesEditsButPendingStays`'s full three-minute budget — the wait never saw its condition arrive — while the whole run was wall-clock-slow at low CPU load (watch tests 3-5x their warm durations), and `FleetDaemonsTests.Json_OldSingleFileBeat_StillAnswersAsOneProcess` failed beside it at 778ms; both were green on isolated reruns within minutes, no source change involved. A cold boot storms the disk and kernel resource pools, and a chain can freeze for its entire budget, which no multiple covers.

Rule: a knife failing at its FULL budget with load low and the rest of the run slow is reproduced warm (isolated, a few times) before the budget or the code is touched — the budget comment's own contract ("a failure now reads as a real stall") stays true, and the stall's cause decides the fix, not the number.

## The gate

Load flake evidence is only valid from a full serial suite run (`dotnet test --no-build -m:1`) on the loaded box — isolated runs are green by definition here. A fix is accepted after three consecutive clean serial runs. Precedent for load-tolerant deadlines: 087-T3 (measured multiple of observed latency, no fixed floor). Bare `Skip` is not a fix; a tolerance must name the invariant it keeps.
