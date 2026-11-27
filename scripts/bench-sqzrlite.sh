#!/usr/bin/env bash
# sqzrlite benchmark baseline — the re-runnable entry for every number in docs/benchmarks/.
# One run prints the whole table: binary facts, full-index wall, startup (one-shot map cold
# and serve-to-first-answer), steady idle RSS/CPU with the watcher armed, per-query p50/p95
# over a scripted 32-query mix, the freshness-check price, and the watcher pass wall after
# a 10-file edit burst with the contention queries fired into the live pass.
#
# Methodology (matches the 130 research doc):
#   - SQUEEZR_HOME is pinned to a fresh mktemp dir outside $HOME for the whole run and
#     removed on exit; the live ~/.squeezr is never opened.
#   - The fixture is generated, not copied: 150 files (120 .cs + 30 .md), the same shape
#     the T3/T5 evidence used. One fixture feeds every number in the run.
#   - Load is printed on every phase line; nothing here asserts absolute times — a rerun
#     on a busier box shifts the numbers, the shape stays.
#   - The freshness-check price line is derived by subtraction (topK=2 arm minus topK=50
#     arm over the same tool) and labeled inferred; every other number is a direct read.
#
# Variants: the published single-file binary is the primary subject. When the Release
# build of src/Squeezr.Lite is present (or --source builds it), the startup block also
# measures the framework-dependent DLL so the single-file win stays visible. --with-full
# adds one like-for-like row: the full product's `squeezr index --no-semantic` on the same
# fixture (the semantic-phase row stays with the research doc, it needs Qdrant/Ollama).
#
# usage:
#   scripts/bench-sqzrlite.sh [--bin PATH] [--no-source] [--with-full] [--no-full]
#                             [--reps N] [--idle-secs N] [--keep]
#   --bin        published sqzrlite to measure (default: artifacts/publish-sqzrlite-<rid>/sqzrlite,
#                published on the spot via scripts/publish-sqzrlite.sh when missing)
#   --no-source  skip the build-from-source startup variant
#   --with-full  force the full-product index row (default: attempted, skipped on build failure)
#   --no-full    skip the full-product index row
#   --reps       startup repetitions per variant (default 5)
#   --idle-secs  idle sampling window in seconds (default 240)
#   --keep       keep the pinned home and fixture for post-mortem (printed on exit)
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bin_path=''
measure_source=true
with_full=auto
reps=5
idle_secs=240
keep=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bin) [[ $# -ge 2 ]] || { echo "bench-sqzrlite: --bin requires an argument" >&2; exit 1; }; bin_path="$2"; shift 2 ;;
    --no-source) measure_source=false; shift ;;
    --with-full) with_full=force; shift ;;
    --no-full) with_full=skip; shift ;;
    --reps) [[ $# -ge 2 ]] || { echo "bench-sqzrlite: --reps requires an argument" >&2; exit 1; }; reps="$2"; shift 2 ;;
    --idle-secs) [[ $# -ge 2 ]] || { echo "bench-sqzrlite: --idle-secs requires an argument" >&2; exit 1; }; idle_secs="$2"; shift 2 ;;
    --keep) keep=true; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "bench-sqzrlite: unknown option: $1" >&2; exit 1 ;;
  esac
done

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/sqzrlite-bench.XXXXXX")"
PINNED_HOME="$SCRATCH/home"
FIXTURE="$SCRATCH/fixture"
SESSION_JSON="$SCRATCH/session.json"
CLEANED=false

cleanup() {
  if $keep; then
    echo "kept (—keep): home=$PINNED_HOME fixture=$FIXTURE session=$SESSION_JSON"
    return
  fi
  rm -rf "$SCRATCH"
  CLEANED=true
}
trap cleanup EXIT

say() { printf '%s\n' "$*"; }
load_line() { printf 'load: %s (1m) %s (5m) %s (15m)\n' "$(cut -d' ' -f1 /proc/loadavg)" "$(cut -d' ' -f2 /proc/loadavg)" "$(cut -d' ' -f3 /proc/loadavg)"; }
die() { printf 'FATAL: %s\n' "$*" >&2; exit 1; }

export SQUEEZR_HOME="$PINNED_HOME"
export NO_COLOR=1
mkdir -p "$PINNED_HOME"

say "== bench: sqzrlite baseline =="
say "date: $(date -u '+%Y-%m-%d %H:%M UTC')"
commit="$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || echo unknown)"
say "commit: $commit ($(git -C "$repo_root" rev-parse --abbrev-ref HEAD 2>/dev/null))"
say "box: $(uname -sr), $(nproc) cores, $(awk '/MemTotal/ {printf "%.0f GB", $2/1024/1024}' /proc/meminfo), dotnet $(dotnet --version 2>/dev/null || echo missing)"
load_line

# ---- binary resolution -------------------------------------------------------

rid='linux-x64'
case "$(uname -m)" in
  aarch64|arm64) rid='linux-arm64' ;;
esac
if [[ -z "$bin_path" ]]; then
  bin_path="$repo_root/artifacts/publish-sqzrlite-$rid/sqzrlite"
  if [[ ! -x "$bin_path" ]]; then
    say "publish: no published binary found, running scripts/publish-sqzrlite.sh"
    "$repo_root/scripts/publish-sqzrlite.sh" >/dev/null || die "publish failed"
  fi
fi
[[ -x "$bin_path" ]] || die "binary not executable: $bin_path"
bin_size=$(stat -c %s "$bin_path")
publish_entries=$(ls -1 "$(dirname "$bin_path")" | wc -l)
publish_so=$(find "$(dirname "$bin_path")" -maxdepth 2 -name '*.so' | wc -l)
say "binary: $bin_path"
say "binary facts: $bin_size B single-file, publish dir holds $publish_entries entries ($publish_so native .so side-cars, rest is the MSBuild BuildHost trees; 0 loose managed DLLs)"
say "publish shape: self-contained single-file + ReadyToRun; trim and NativeAOT are rejected by measurement (Spectre.Console.Cli reflection dies, exit 134) — scripts/publish-sqzrlite.sh header and audits/130-t5-2026-11-27/ carry the proof"

lite_dll="$repo_root/src/Squeezr.Lite/bin/Release/net10.0/sqzrlite.dll"
if $measure_source; then
  if [[ ! -f "$lite_dll" ]]; then
    say "build: Release sqzrlite.dll (build-from-source variant)"
    dotnet build "$repo_root/src/Squeezr.Lite" -c Release --nologo >/dev/null || die "source build failed"
  fi
  [[ -f "$lite_dll" ]] || die "Release sqzrlite.dll missing after build: $lite_dll"
  build_dll_count=$(find "$(dirname "$lite_dll")" -maxdepth 1 -name '*.dll' | wc -l)
  say "source variant: dotnet $lite_dll ($build_dll_count DLLs in the build output)"
fi

full_dll=''
if [[ "$with_full" != skip ]]; then
  full_dll="$repo_root/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll"
  if [[ ! -f "$full_dll" ]]; then
    say "build: Release squeezr.dll (full-product comparison row)"
    if ! dotnet build "$repo_root/src/Squeezr.Cli" -c Release --nologo >/dev/null; then
      if [[ "$with_full" == force ]]; then die "full-product build failed"; fi
      say "full-product row SKIPPED: squeezr.dll build failed"
      full_dll=''
    fi
  fi
  if [[ -n "${full_dll:-}" && ! -f "$full_dll" ]]; then
    if [[ "$with_full" == force ]]; then die "full squeezr.dll missing: $full_dll"; fi
    say "full-product row SKIPPED: $full_dll missing"
    full_dll=''
  fi
fi

# ---- fixture -----------------------------------------------------------------

gen_fixture() {
  local root=$1 i name out
  mkdir -p "$root/src" "$root/docs"
  local -a names=(OrderService PaymentGateway LedgerEntry Reconciler VaultClient RateLimiter)
  for ((i = 0; i < 120; i++)); do
    name=${names[i % 6]}
    out="$root/src/Gen$(printf '%03d' "$i").cs"
    {
      printf 'namespace Fixture.Gen%d;\n\n' "$i"
      printf 'public class %s%d\n{\n' "$name" "$i"
      printf '    private readonly %sHelper%d _helper = new("%sHelper%d");\n\n' "$name" "$i" "$name" "$i"
      printf '    /// <summary>Handles request %d for %s.</summary>\n' "$i" "$name"
      printf '    public int Handle%d(int amount)\n    {\n' "$i"
      printf '        var scaled = amount * %d;\n' "$((i + 1))"
      printf '        return _helper.Scale(scaled) + %d;\n    }\n\n' "$i"
      printf '    public string Name() => "%s%d";\n}\n\n' "$name" "$i"
      printf 'public class %sHelper%d\n{\n' "$name" "$i"
      printf '    private readonly string _tag;\n'
      printf '    public %sHelper%d(string tag) => _tag = tag;\n' "$name" "$i"
      printf '    public int Scale(int value) => value * 2 + _tag.Length;\n}\n'
    } > "$out"
  done
  for ((i = 0; i < 30; i++)); do
    name=${names[i % 6]}
    printf '# Note %d\n\nOperation %d is handled by %s. See Gen%03d for the handler.\n' "$i" "$i" "$name" "$i" \
      > "$root/docs/note$(printf '%03d' "$i").md"
  done
}

gen_fixture "$FIXTURE/repo"
fixture_files=$(find "$FIXTURE" -type f | wc -l)
fixture_cs=$(find "$FIXTURE" -name '*.cs' | wc -l)
fixture_bytes=$(du -sb "$FIXTURE" | cut -f1)
say "== fixture =="
say "fixture: $FIXTURE — $fixture_files files ($fixture_cs .cs, $((fixture_files - fixture_cs)) .md), $fixture_bytes B, generated fresh for this run"

# ---- full index --------------------------------------------------------------

run_timed_index() {
  local -a cmd=("$@")
  local tmp="$SCRATCH/time.txt"
  ( cd "$FIXTURE" && /usr/bin/time -v -o "$tmp" "${cmd[@]}" >/dev/null 2>&1 ) || die "index run failed: ${cmd[*]}"
  local wall rss
  wall=$(sed -n 's/.*wall clock.*): //p' "$tmp")
  rss=$(sed -n 's/.*Maximum resident set size.*: //p' "$tmp")
  printf '%s|%s' "$wall" "$rss"
}

say "== full index =="
setup_log="$SCRATCH/setup.log"
( cd "$FIXTURE" && "$bin_path" init --json >/dev/null 2>&1 && "$bin_path" add repo --name fixture --json >>"$setup_log" 2>&1 ) || { cat "$setup_log" >&2; die "lite init/add failed"; }
ifs_out=$(run_timed_index "$bin_path" index --json)
lite_counts=$( cd "$FIXTURE" && "$bin_path" status --json 2>/dev/null | python3 -c "import json,sys; r=json.load(sys.stdin)['repos'][0]; print(r['files'],'files,',r['symbols'],'symbols,',r['chunks'],'chunks')" )
say "lite index (published): wall ${ifs_out%%|*} | max rss ${ifs_out##*|} KB | $lite_counts"
load_line

if [[ -n "${full_dll:-}" && -f "$full_dll" ]]; then
  FULL_HOME="$SCRATCH/full-home"
  mkdir -p "$FULL_HOME"
  if ( cd "$FIXTURE" && SQUEEZR_HOME="$FULL_HOME" dotnet "$full_dll" init --json >/dev/null 2>>"$setup_log" && SQUEEZR_HOME="$FULL_HOME" dotnet "$full_dll" add repo --name fixture --json >/dev/null 2>>"$setup_log" ); then
    ffs_out=$(run_timed_index env SQUEEZR_HOME="$FULL_HOME" dotnet "$full_dll" index --no-semantic --json)
    say "full product index --no-semantic (same fixture): wall ${ffs_out%%|*} | max rss ${ffs_out##*|} KB | the semantic-phase row lives in the research doc (598 s / 1.11 GB on a 1165-file fixture) and needs Qdrant/Ollama"
    rm -rf "$FULL_HOME"
    load_line
  else
    say "full product row SKIPPED: init/add failed under $FULL_HOME"
  fi
fi

# ---- embedded drivers --------------------------------------------------------

# Startup probes: one-shot map cold and serve-to-first-answer, both over real stdio.
cat > "$SCRATCH/probe_startup.py" <<'PYEOF'
import json, os, select, subprocess, sys, time

def read_msg(proc, want_id, deadline):
    while True:
        left = deadline - time.monotonic()
        if left <= 0:
            raise TimeoutError(f"no response for id {want_id}")
        r, _, _ = select.select([proc.stdout], [], [], min(left, 5))
        if not r:
            continue
        line = proc.stdout.readline()
        if not line:
            raise EOFError("serve closed stdout")
        msg = json.loads(line)
        if msg.get("id") == want_id:
            return msg

def spawn(cmd, cwd):
    return subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, cwd=cwd, text=True, bufsize=1)

def one_rep(cmd, cwd):
    t0 = time.monotonic()
    proc = spawn(cmd, cwd)
    init_req = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
                "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                            "clientInfo": {"name": "bench", "version": "0.0.0"}}}
    proc.stdin.write(json.dumps(init_req) + "\n"); proc.stdin.flush()
    read_msg(proc, 1, t0 + 60)
    t_init = time.monotonic()
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); proc.stdin.flush()
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                                 "params": {"name": "repo_map", "arguments": {"depth": 2}}}) + "\n"); proc.stdin.flush()
    msg = read_msg(proc, 2, t0 + 60)
    t_answer = time.monotonic()
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
    return {"init_ms": round((t_init - t0) * 1000), "first_answer_ms": round((t_answer - t0) * 1000)}

reps = int(sys.argv[1]); cwd = sys.argv[2]; cmd = sys.argv[3:]
out = [one_rep(cmd, cwd) for _ in range(reps)]
print(json.dumps(out))
PYEOF

# The long session: idle sampling, query mix, check-price A/B, burst + contention, post-pass.
cat > "$SCRATCH/serve_session.py" <<'PYEOF'
import json, os, select, statistics, subprocess, sys, time

PAGE = os.sysconf("SC_PAGE_SIZE")
CLK = os.sysconf("SC_CLK_TCK")
EDITED = [5, 16, 27, 38, 49, 60, 71, 82, 93, 104]
NAMES = ["OrderService", "PaymentGateway", "LedgerEntry", "Reconciler", "VaultClient", "RateLimiter"]

def read_msg(proc, want_id, deadline):
    while True:
        left = deadline - time.monotonic()
        if left <= 0:
            raise TimeoutError(f"no response for id {want_id}")
        r, _, _ = select.select([proc.stdout], [], [], min(left, 5))
        if not r:
            continue
        line = proc.stdout.readline()
        if not line:
            raise EOFError("serve closed stdout")
        msg = json.loads(line)
        if msg.get("id") == want_id:
            return msg

def call(proc, next_id, name, args, deadline_s=60):
    t0 = time.monotonic()
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": "tools/call",
                                 "params": {"name": name, "arguments": args}}) + "\n")
    proc.stdin.flush()
    msg = read_msg(proc, next_id, t0 + deadline_s)
    wall_ms = round((time.monotonic() - t0) * 1000)
    envelope = {}
    raw = None
    if msg.get("isError") or not msg.get("result", {}).get("content"):
        raw = json.dumps(msg)[:400]
    else:
        try:
            envelope = json.loads(msg["result"]["content"][0]["text"])
        except (KeyError, IndexError, ValueError):
            raw = json.dumps(msg)[:400]
    return wall_ms, envelope, raw

def sample_rss(pid):
    with open(f"/proc/{pid}/statm") as f:
        return int(f.read().split()[1]) * PAGE // 1024

def sample_ticks(pid):
    with open(f"/proc/{pid}/stat") as f:
        parts = f.read().split()
    return int(parts[13]) + int(parts[14])

def pctl(values, q):
    values = sorted(values)
    if len(values) == 1:
        return values[0]
    pos = q * (len(values) - 1)
    lo = int(pos); frac = pos - lo
    if lo + 1 >= len(values):
        return values[-1]
    return values[lo] + (values[lo + 1] - values[lo]) * frac

idle_secs = int(sys.argv[1]); cwd = sys.argv[2]; stderr_path = sys.argv[3]; out_path = sys.argv[4]
mode = sys.argv[5]
cmd = sys.argv[6:]
report = {}

stderr_file = open(stderr_path, "w")
proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=stderr_file, cwd=cwd, text=True, bufsize=1)
next_id = 10

t0 = time.monotonic()
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                             "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                                        "clientInfo": {"name": "bench", "version": "0.0.0"}}}) + "\n")
proc.stdin.flush()
read_msg(proc, 1, t0 + 60)
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

armed = False
arm_deadline = time.monotonic() + 30
stderr_file.flush()
while not armed and time.monotonic() < arm_deadline:
    time.sleep(0.2)
    with open(stderr_path) as f:
        armed = "watch engine running" in f.read()
report["watch_armed"] = armed

pid = proc.pid
ticks_a = sample_ticks(pid)
idle_wall_a = time.monotonic()
rss_samples = []
end = time.monotonic() + idle_secs
while time.monotonic() < end:
    rss_samples.append(sample_rss(pid))
    time.sleep(2.0)
ticks_b = sample_ticks(pid)
idle_wall_b = time.monotonic()
report["idle"] = {
    "window_s": round(idle_wall_b - idle_wall_a, 1),
    "rss_min_kb": min(rss_samples), "rss_avg_kb": round(statistics.mean(rss_samples)),
    "rss_max_kb": max(rss_samples),
    "cpu_pct": round((ticks_b - ticks_a) / CLK / (idle_wall_b - idle_wall_a) * 100, 2),
}

if mode == "idle-only":
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
    stderr_file.close()
    with open(out_path, "w") as f:
        json.dump(report, f, indent=1)
    sys.exit(0)

MIX = ([("code_search", a) for a in [
    {"query": "VaultClient17", "topK": 3}, {"query": "LedgerEntry8", "topK": 3},
    {"query": "RateLimiter45", "topK": 3}, {"query": "OrderService90", "topK": 3},
    {"query": "public class PaymentGateway", "topK": 8},
    {"query": "_helper.Scale", "topK": 10, "kind": "code"},
    {"query": "Operation 1[0-9] is handled", "regex": True, "kind": "docs", "topK": 5},
    {"query": "Scale", "prefix": "src/", "topK": 10},
    {"query": "Note 2[0-9]", "kind": "docs", "topK": 8},
    {"query": "Reconciler", "topK": 6},
    {"query": "Handles request 66", "topK": 5},
    {"query": "Name() =>", "topK": 10, "kind": "code"}]] +
 [("symbol_lookup", {"name": f"{n}{i}"}) for n, i in [
     ("OrderService", 12), ("PaymentGateway", 25), ("Reconciler", 33),
     ("VaultClient", 40), ("RateLimiter", 71), ("LedgerEntry", 104)]] +
 [("find_references", {"symbol": f"{n}Helper{i}"}) for n, i in [
     ("OrderService", 12), ("PaymentGateway", 25), ("Reconciler", 33),
     ("VaultClient", 40), ("RateLimiter", 71), ("LedgerEntry", 104)]] +
 [("repo_map", a) for a in [
     {"depth": 1}, {"depth": 2}, {"depth": 3},
     {"prefix": "src/", "depth": 2}, {"prefix": "docs/", "depth": 2}, {"depth": 1, "prefix": "src/"}]] +
 [("index_status", {}) for _ in range(2)])

mix_rows = []
for name, args in MIX:
    wall_ms, envelope, raw = call(proc, next_id, name, args); next_id += 1
    fresh = envelope.get("freshness") or {}
    mix_rows.append({"tool": name, "ms": wall_ms, "checked": fresh.get("checked", 0),
                     "staleTotal": fresh.get("staleTotal", 0),
                     "refreshed": fresh.get("refreshed"), "staleFiles": envelope.get("staleFiles"),
                     "raw": raw})
report["mix"] = mix_rows

ab = []
for rep in range(5):
    w1, e1, r1 = call(proc, next_id, "code_search", {"query": "OrderServiceHelper11", "topK": 2}); next_id += 1
    w2, e2, r2 = call(proc, next_id, "code_search", {"query": "_helper.Scale", "topK": 50}); next_id += 1
    ab.append({"arm": "topK=2", "ms": w1, "checked": (e1.get("freshness") or {}).get("checked", 0), "raw": r1})
    ab.append({"arm": "topK=50", "ms": w2, "checked": (e2.get("freshness") or {}).get("checked", 0), "raw": r2})
report["check_price_arms"] = ab

def poll_pass(state):
    with open(stderr_path) as f:
        f.seek(state["pos"])
        new = f.read()
        state["pos"] = f.tell()
    return [ln for ln in new.splitlines() if "watch pass +" in ln]

def run_burst(indices, queries, sleep_s):
    global next_id
    state = {"pos": stderr_pos[0]}
    t0 = time.monotonic()
    edited = []
    for i in indices:
        name = NAMES[i % 6]
        path = os.path.join(cwd, "repo", "src", f"Gen{i:03d}.cs")
        with open(path) as f:
            text = f.read()
        old = f"/// <summary>Handles request {i} for {name}.</summary>"
        new = f"/// <summary>Handles request {i} for {name}, revised by the burst probe.</summary>"
        with open(path, "w") as f:
            f.write(text.replace(old, new))
        edited.append((i, name))
    edit_done = time.monotonic()

    rows = []
    pass_line = None
    pass_seen = None
    for name, args in queries:
        wall_ms, envelope, raw = call(proc, next_id, "code_search", args, deadline_s=90); next_id += 1
        fresh = envelope.get("freshness") or {}
        rows.append({"ms": wall_ms, "checked": fresh.get("checked", 0),
                     "staleTotal": fresh.get("staleTotal", 0),
                     "refreshed": fresh.get("refreshed"),
                     "staleFiles": envelope.get("staleFiles"), "raw": raw})
        found = poll_pass(state)
        if found and pass_line is None:
            pass_line = found[0]
            pass_seen = time.monotonic()
        if sleep_s:
            time.sleep(sleep_s)
    deadline = time.monotonic() + 120
    while pass_line is None and time.monotonic() < deadline:
        found = poll_pass(state)
        if found:
            pass_line = found[0]
            pass_seen = time.monotonic()
        else:
            time.sleep(0.25)
    stderr_pos[0] = state["pos"]
    return {
        "edited_count": len(edited),
        "edited_files": [f"repo/src/Gen{i:03d}.cs" for i, _ in edited],
        "queries": rows,
        "pass_line": pass_line,
        "edit_to_completion_s": round(pass_seen - edit_done, 2) if pass_seen else None,
        "total_s": round(time.monotonic() - t0, 1),
    }

stderr_pos = [0]
burst10_queries = [("code_search", {"query": f"Handles request {i} for {NAMES[i % 6]}", "topK": 5})
                   for i in EDITED]
report["burst10"] = run_burst(EDITED, burst10_queries, sleep_s=0.5)

BURST30 = [6, 11, 22, 28, 34, 39, 44, 50, 55, 61, 66, 72, 77, 83, 88, 94, 99, 105, 110, 115,
           0, 17, 23, 35, 46, 52, 63, 74, 85, 96]
burst30_queries = [("code_search", {"query": f"Handles request {i} for {NAMES[i % 6]}", "topK": 5})
                   for i in BURST30[:10]] * 2
time.sleep(3)
report["burst30"] = run_burst(BURST30, burst30_queries, sleep_s=0)

post = []
for name, args in [("code_search", {"query": "burst probe revised", "topK": 10}),
                   ("code_search", {"query": "VaultClient17", "topK": 3}),
                   ("repo_map", {"depth": 2})]:
    wall_ms, envelope, raw = call(proc, next_id, name, args, deadline_s=90); next_id += 1
    fresh = envelope.get("freshness") or {}
    post.append({"tool": name, "ms": wall_ms, "staleTotal": fresh.get("staleTotal", 0),
                 "refreshed": fresh.get("refreshed"), "staleFiles": envelope.get("staleFiles"),
                 "raw": raw})
report["post_pass"] = post

proc.terminate()
try:
    proc.wait(timeout=5)
except subprocess.TimeoutExpired:
    proc.kill()
stderr_file.close()

with open(out_path, "w") as f:
    json.dump(report, f, indent=1)
PYEOF

# ---- startup -----------------------------------------------------------------

map_reps() {
  local -a cmd=("$@")
  local i
  local tmp="$SCRATCH/one.txt"
  : > "$SCRATCH/one-rows.txt"
  for ((i = 1; i <= reps; i++)); do
    ( cd "$FIXTURE" && /usr/bin/time -f "%e %M" -o "$tmp" "${cmd[@]}" >/dev/null 2>&1 ) || die "map rep failed: ${cmd[*]}"
    tr '\n' ' ' < "$tmp" >> "$SCRATCH/one-rows.txt"
    printf '\n' >> "$SCRATCH/one-rows.txt"
  done
  python3 - "$SCRATCH/one-rows.txt" <<'PYEOF'
import statistics, sys
walls, peaks = [], []
for line in open(sys.argv[1]):
    parts = line.split()
    if len(parts) >= 2:
        walls.append(float(parts[0]))
        peaks.append(float(parts[1]))
print(f"reps={len(walls)} walls={walls}s median_wall={statistics.median(walls):.2f}s "
      f"avg_peak_rss={statistics.mean(peaks)/1024:.0f}MB")
PYEOF
}

say "== startup: one-shot map cold (each rep a fresh process on the warm store) =="
say "published: $(map_reps "$bin_path" map --repo fixture --depth 2 --json)"
if $measure_source; then
  say "source:    $(map_reps dotnet "$lite_dll" map --repo fixture --depth 2 --json)"
fi
load_line

say "== startup: serve spawn -> initialize -> first repo_map answer (real stdio) =="
pub_first=$(python3 "$SCRATCH/probe_startup.py" "$reps" "$FIXTURE" "$bin_path" serve)
echo "$pub_first" | python3 -c '
import json, statistics, sys
rows = json.load(sys.stdin)
init = [r["init_ms"] for r in rows]; ans = [r["first_answer_ms"] for r in rows]
print(f"published: init {[x / 1000 for x in init]} s -> {[x / 1000 for x in ans]} s first answer (median {statistics.median(ans) / 1000:.3f} s)")'
if $measure_source; then
  src_first=$(python3 "$SCRATCH/probe_startup.py" "$reps" "$FIXTURE" dotnet "$lite_dll" serve)
  echo "$src_first" | python3 -c '
import json, statistics, sys
rows = json.load(sys.stdin)
ans = [r["first_answer_ms"] for r in rows]
print(f"source:    first answer {[x / 1000 for x in ans]} s (median {statistics.median(ans) / 1000:.3f} s)")'
fi
load_line

# ---- the serve session -------------------------------------------------------

say "== steady idle: serve --watch, converged store, no queries =="
say "sampling /proc statm+stat every 2 s for ${idle_secs}s (published)..."
python3 "$SCRATCH/serve_session.py" "$idle_secs" "$FIXTURE" "$SCRATCH/serve-stderr.log" "$SESSION_JSON" full "$bin_path" serve --watch
if $measure_source; then
  say "sampling /proc statm+stat every 2 s for ${idle_secs}s (build-from-source)..."
  python3 "$SCRATCH/serve_session.py" "$idle_secs" "$FIXTURE" "$SCRATCH/serve-stderr-src.log" "$SCRATCH/session-src.json" idle-only dotnet "$lite_dll" serve --watch
fi
load_line

src_json_args=()
$measure_source && src_json_args=("$SCRATCH/session-src.json")
python3 - "$SESSION_JSON" "${src_json_args[@]}" <<'PYEOF'
import json, statistics, sys

report = json.load(open(sys.argv[1]))
idle = report["idle"]
print(f"idle (published): rss {idle['rss_min_kb']/1024:.1f}/{idle['rss_avg_kb']/1024:.1f}/{idle['rss_max_kb']/1024:.1f} MB min/avg/max "
      f"over {idle['window_s']:.0f}s, cpu {idle['cpu_pct']}% (of one core), watch armed: {report['watch_armed']}")

by_tool = {}
for row in report["mix"]:
    by_tool.setdefault(row["tool"], []).append(row["ms"])
anomalies = [r for r in report["mix"] + report["burst10"]["queries"] + report["burst30"]["queries"]
             + report["post_pass"] if r.get("raw")]
if anomalies:
    print(f"anomalies (unparseable envelopes, {len(anomalies)}):")
    for r in anomalies[:6]:
        print(f"  {r.get('tool', r.get('edited_file', '?'))}: {r['raw']}")
print("query mix (converged store, guard on):")
for tool, walls in by_tool.items():
    print(f"  {tool:16s} n={len(walls):2d} p50={statistics.median(walls):6.0f}ms p95={sorted(walls)[int(0.95*(len(walls)-1))]:6.0f}ms")
all_walls = [r["ms"] for r in report["mix"]]
checked = [r["checked"] for r in report["mix"]]
stale = sum(r["staleTotal"] for r in report["mix"])
print(f"  {'overall':16s} n={len(all_walls):2d} p50={statistics.median(all_walls):6.0f}ms p95={sorted(all_walls)[int(0.95*(len(all_walls)-1))]:6.0f}ms "
      f"| checked/query {min(checked)}-{max(checked)}, staleTotal sum {stale}")

arms = {}
for row in report["check_price_arms"]:
    arms.setdefault(row["arm"], []).append((row["ms"], row["checked"]))
med = {a: (statistics.median([w for w, _ in rows]), statistics.median([c for _, c in rows])) for a, rows in arms.items()}
if "topK=2" in med and "topK=50" in med:
    dm = med["topK=50"][0] - med["topK=2"][0]
    dc = med["topK=50"][1] - med["topK=2"][1]
    price = f"{dm / dc:.2f} ms/candidate" if dc > 0 else "n/a (same candidate count)"
    print(f"check price: topK=2 median {med['topK=2'][0]:.0f}ms / {med['topK=2'][1]:.0f} candidates, "
          f"topK=50 median {med['topK=50'][0]:.0f}ms / {med['topK=50'][1]:.0f} candidates -> ~{price} [inferred by subtraction]")

for burst_name in ("burst10", "burst30"):
    b = report[burst_name]
    rows = b["queries"]
    walls = [r["ms"] for r in rows]
    stale_hits = [r for r in rows if r["staleTotal"] and r["staleTotal"] > 0]
    healed = sum(r["refreshed"] or 0 for r in rows)
    named = sorted({f for r in rows for f in (r["staleFiles"] or [])})
    print(f"{burst_name} ({b['edited_count']} files edited): during-burst queries n={len(walls)}, "
          f"wall {min(walls)}-{max(walls)}ms, inline heals {healed}, stale reports {len(stale_hits)}"
          + (f" (files named: {', '.join(named)})" if named else ""))
    print(f"  pass line: {b['pass_line'] or 'NOT SEEN'} | edit->completion {b['edit_to_completion_s']}s | burst total {b['total_s']}s")
post_walls = [r["ms"] for r in report["post_pass"]]
post_stale = sum(r["staleTotal"] for r in report["post_pass"])
print(f"post-pass: walls {post_walls}ms, staleTotal sum {post_stale}")
src_idle_path = sys.argv[2] if len(sys.argv) > 2 else None
if src_idle_path:
    src = json.load(open(src_idle_path))["idle"]
    print(f"idle (source):    rss {src['rss_min_kb']/1024:.1f}/{src['rss_avg_kb']/1024:.1f}/{src['rss_max_kb']/1024:.1f} MB min/avg/max "
          f"over {src['window_s']:.0f}s, cpu {src['cpu_pct']}% (of one core)")
PYEOF

say "== done =="
load_line
date -u '+%Y-%m-%d %H:%M UTC'
