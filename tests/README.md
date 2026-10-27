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

The 100 fix covered the drain family's saturated member, but the class's other members kept a shared 30s budget over a chain of FSW delivery + one debounce window + a full pass — and `ServeWatchClaimTests.TwoParticipants_OnOneStore_OwnerIndexes_ObserverAnswers` died at 60s the same day (104's repro run 1, load spiked to 44 on the multi-lane box): the chain idles at 2-6s isolated but stretches an order of magnitude when other lanes load the host. The budget is a multiple of the observed chain latency (087-T3 shape), documented at the assert site — 100 picked three minutes for the seven-window chain, 104 moved the one-window members onto the same size. Related margin rule: a test-side stop wait must exceed the machinery's internal join window (`WorkspaceWatcher.PassJoinTimeout` = 5s), never match it — matching gives a loaded box zero margin and fails the test on a stop that was still working.

Rule: size wait budgets from measured chain latency under load and name the multiple; a test that joins or stops machinery with its own timeout must budget strictly more than the machinery's internal join.

## The gate

Load flake evidence is only valid from a full serial suite run (`dotnet test --no-build -m:1`) on the loaded box — isolated runs are green by definition here. A fix is accepted after three consecutive clean serial runs. Precedent for load-tolerant deadlines: 087-T3 (measured multiple of observed latency, no fixed floor). Bare `Skip` is not a fix; a tolerance must name the invariant it keeps.
