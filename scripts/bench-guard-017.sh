#!/usr/bin/env bash
# 017 measurement carrier — the repeatable entry for every number the feature ships.
# Replaces the one-off drivers of the arms past (run-arms.sh, run-t2-arms.sh and their probes lived
# under dead scratch directories and could not be re-run; the report now cites this script instead).
#
# Carriers (all from git, all built outside the repo tree):
#   before     = COMMIT_BEFORE (08036ec — parent of the feature's first code commit 51794ec,
#                merge-base with main, the state audits/baseline-017-2026-11-09.md was shot on)
#   prebudget  = COMMIT_PREBUDGET (506510d — T1+T2+T3 closed, no refresh budget; the before column
#                of the budget-wire comparison, because a budget-less guard on 08036ec would mix
#                the check-path price and the budget price into one arm)
#   after      = the feature branch HEAD at invocation time
#
# Methodology the contract demands: >= REPS repetitions per arm, arms rotated across two batches,
# order swap between batches (the control against "the first process on the box pays the warming"),
# load printed on every arm line, absolutes never compared across batches — form and within-run
# ratios only. Response cache is in-process and identical repeat calls over MCP are served in ~2 ms,
# so every wire rep gets its own serve process and arguments differ per scenario.
#
# Safety: SQUEEZR_HOME is pinned to the scratch home the stands are registered in for every stand
# run, and every build gets a fresh mktemp home that is deleted on exit. The live ~/.squeezr is
# never opened. No files are written into the git repo — probes, carriers, logs and named sets all
# live under SCRATCH. `plan` / --dry-run writes nothing at all.
#
# usage:
#   bench-guard-017.sh plan                          resolved config, zero writes
#   bench-guard-017.sh prepare                       carriers + probes (builds, fresh homes)
#   bench-guard-017.sh stands                        generate+index+snapshot what is missing
#   bench-guard-017.sh guard <1|2>                   guard-path before/after on three stands
#   bench-guard-017.sh structure <1|2>               map render + structure pass + cap-50 (after)
#   bench-guard-017.sh equiv <stand-root>            FindFile vs ListFiles equivalence walk
#   bench-guard-017.sh wire-capture                  named set from the after carrier, clean store
#   bench-guard-017.sh wire <1|2>                    budget wire arms: prebudget vs after, 12/24 dirty
#   bench-guard-017.sh verify                        store hashes + content audit + structure pass
#   bench-guard-017.sh cleanup                       remove the temp homes this script made
#
# configuration (all env, all optional — this file hardcodes no scratch directory):
#   BENCH_REPO  git repo the carriers are archived from (default: the repo containing this script)
#   SCRATCH     work root for carriers/probes/logs/homes (default: $BENCH_REPO/../bench-guard-017)
#   POLY_ROOT CONTROL_ROOT REAL_ROOT  stand roots (defaults under $SCRATCH/stands)
#   STAND_HOME  the pinned SQUEEZR_HOME the stands are registered in (default: $SCRATCH/home)
#   PRISTINE_DIR store snapshots (default: $SCRATCH/store-pristine; required for prebuilt stands)
#   COMMIT_BEFORE COMMIT_PREBUDGET    carrier anchors (defaults 08036ec / 506510d)
#   COMMIT_AFTER  default: HEAD of BENCH_REPO
#   REPS WIRE_REPS                    7 / 5 by default
#   REAL_SOURCE REAL_REF              repo + ref to generate the real stand from (generation only)
set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
BENCH_REPO=${BENCH_REPO:-$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)}
SCRATCH=${SCRATCH:-"$(dirname "$BENCH_REPO")/bench-guard-017"}
COMMIT_BEFORE=${COMMIT_BEFORE:-08036ec}
COMMIT_PREBUDGET=${COMMIT_PREBUDGET:-506510d}
COMMIT_AFTER=${COMMIT_AFTER:-$(git -C "$BENCH_REPO" rev-parse HEAD)}
POLY_ROOT=${POLY_ROOT:-$SCRATCH/stands/poly-12k}
CONTROL_ROOT=${CONTROL_ROOT:-$SCRATCH/stands/poly-1.2k}
REAL_ROOT=${REAL_ROOT:-$SCRATCH/stands/real}
STAND_HOME=${STAND_HOME:-$SCRATCH/home}
PRISTINE_DIR=${PRISTINE_DIR:-$SCRATCH/store-pristine}
REPS=${REPS:-7}
WIRE_REPS=${WIRE_REPS:-5}
DIRTY_COUNTS=${DIRTY_COUNTS:-"12 24"}
GEN_DIRS=${GEN_DIRS:-120}
GEN_FILES=${GEN_FILES:-100}
GEN_CTRL_DIRS=${GEN_CTRL_DIRS:-12}
REAL_SOURCE=${REAL_SOURCE:-}
REAL_REF=${REAL_REF:-HEAD}

CARRIERS=$SCRATCH/carriers
PROBES=$SCRATCH/probes
LOGS=$SCRATCH/logs
NAMED=$SCRATCH/named.json
AFTER_DLL=$CARRIERS/after/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll
PREBUDGET_DLL=$CARRIERS/prebudget/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll
STORE_FILES="meta.db trigram.idx config.json workspace.json"

say() { printf '%s\n' "$*"; }
die() { printf 'FATAL: %s\n' "$*" >&2; exit 1; }
load1() { cut -d' ' -f1 /proc/loadavg; }

fresh_home() {
  local h
  h=$(mktemp -d "$SCRATCH/home-XXXXXX")
  FRESH_HOMES+=("$h")
  export SQUEEZR_HOME="$h"
}
FRESH_HOMES=()
atexit() {
  local h
  for h in "${FRESH_HOMES[@]:-}"; do [ -n "$h" ] && rm -rf "$h"; done
}
trap atexit EXIT

store_key() {
  local root=$1
  [ -f "$STAND_HOME/workspaces.json" ] || return 1
  python3 -c '
import json, sys, os
root = os.path.realpath(sys.argv[1])
for row in json.load(open(sys.argv[2])):
    if os.path.realpath(row.get("rootPath", "")) == root:
        print(row["storeKey"]); break
' "$root" "$STAND_HOME/workspaces.json"
}

verify_store() {
  local root=$1 key
  key=$(store_key "$root") || { say "STORE-UNREGISTERED $root"; return 1; }
  [ -d "$PRISTINE_DIR/$key" ] || { say "PRISTINE-MISSING $key ($PRISTINE_DIR)"; return 1; }
  say "--- $key vs pristine"
  (cd "$STAND_HOME/workspaces" && sha256sum -c "$PRISTINE_DIR/$key.sha") 2>&1
}

restore_store() {
  local root=$1 key f
  key=$(store_key "$root") || die "stand $root is not registered in $STAND_HOME"
  [ -d "$PRISTINE_DIR/$key" ] || die "no pristine snapshot for $key under $PRISTINE_DIR"
  for f in $STORE_FILES; do
    [ -f "$PRISTINE_DIR/$key/$f" ] || continue
    cp "$PRISTINE_DIR/$key/$f" "$STAND_HOME/workspaces/$key/$f"
    touch -r "$PRISTINE_DIR/$key/$f" "$STAND_HOME/workspaces/$key/$f"
  done
  rm -f "$STAND_HOME/workspaces/$key/queries.jsonl" "$STAND_HOME/workspaces/$key/queries.lock"
}

cmd_plan() {
  say "repo:        $BENCH_REPO"
  say "scratch:     $SCRATCH"
  say "carriers:    before=$COMMIT_BEFORE prebudget=$COMMIT_PREBUDGET after=$COMMIT_AFTER"
  say "stands:      poly=$POLY_ROOT control=$CONTROL_ROOT real=$REAL_ROOT"
  say "stand-home:  $STAND_HOME"
  say "pristine:    $PRISTINE_DIR"
  say "reps:        guard=$REPS wire=$WIRE_REPS dirty=[$DIRTY_COUNTS]"
  say "after-head:  $(git -C "$BENCH_REPO" log --oneline -1)"
  say "disk free:   $(df -h "$SCRATCH" 2>/dev/null | tail -1)"
  say "load:        $(load1)"
}

cmd_prepare() {
  mkdir -p "$SCRATCH" "$CARRIERS" "$PROBES" "$LOGS"
  local carrier commit
  for pair in "before $COMMIT_BEFORE" "prebudget $COMMIT_PREBUDGET" "after $COMMIT_AFTER"; do
    carrier=${pair% *}; commit=${pair#* }
    rm -rf "$CARRIERS/$carrier"
    mkdir -p "$CARRIERS/$carrier"
    git -C "$BENCH_REPO" archive "$commit" | tar -x -C "$CARRIERS/$carrier"
    say "archived $commit -> $CARRIERS/$carrier"
  done
  fresh_home
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  say "=== Release builds: serve carriers (prebudget, after)"
  dotnet build "$CARRIERS/prebudget/src/Squeezr.Cli/Squeezr.Cli.csproj" -c Release -v q --nologo 2>&1 | tail -2
  dotnet build "$CARRIERS/after/src/Squeezr.Cli/Squeezr.Cli.csproj" -c Release -v q --nologo 2>&1 | tail -2
  dotnet build "$CARRIERS/before/src/Squeezr.Core/Squeezr.Core.csproj" -c Debug -v q --nologo 2>&1 | tail -2
  dotnet build "$CARRIERS/after/src/Squeezr.Core/Squeezr.Core.csproj" -c Debug -v q --nologo 2>&1 | tail -2
  say "=== assembly identities (subject: the built assembly, not the source text)"
  local dll
  for dll in \
    "$CARRIERS/prebudget/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll" \
    "$CARRIERS/after/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll" \
    "$CARRIERS/prebudget/src/Squeezr.Core/bin/Release/net10.0/Squeezr.Core.dll" \
    "$CARRIERS/after/src/Squeezr.Core/bin/Release/net10.0/Squeezr.Core.dll" \
    "$CARRIERS/before/src/Squeezr.Core/bin/Debug/net10.0/Squeezr.Core.dll" \
    "$CARRIERS/after/src/Squeezr.Core/bin/Debug/net10.0/Squeezr.Core.dll"; do
    [ -f "$dll" ] && say "$(sha256sum "$dll")"
  done
  write_probes
  say "PROBE-ASSETS=$PROBES"
}

write_probes() {
  mkdir -p "$PROBES/GuardProbe" "$PROBES/StructureProbe" "$PROBES/EquivProbe" "$PROBES/StandAudit"

  cat > "$PROBES/probe.csproj.tmpl" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{ENTRY}.cs" />
    <ProjectReference Include="{CORE}" />
  </ItemGroup>
</Project>
CSPROJ

  cat > "$PROBES/gen.py" <<'GENEOF'
import os, random
seed = int(os.environ.get("GEN_SEED", "20261109"))
dirs = int(os.environ.get("GEN_DIRS", "120"))
files_per_dir = int(os.environ.get("GEN_FILES", "100"))
root = os.environ.get("GEN_ROOT", "polygon")
random.seed(seed)
n = 0
for d in range(dirs):
    p = os.path.join(root, "src", f"module{d:03d}")
    os.makedirs(p, exist_ok=True)
    for f in range(files_per_dir):
        lines = ["using System;"]
        for l in range(random.randint(15, 60)):
            lines.append(f"public class Type{d}_{f}_{l} {{ int Value = {l}; }}")
        open(os.path.join(p, f"Unit{f:03d}.cs"), "w").write("\n".join(lines) + "\n")
        n += 1
print("files:", n)
GENEOF

  cat > "$PROBES/GuardProbe/GuardProbe.cs" <<'GUARDEOF'
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

// Prices FreshnessGuard's check path on a stand, per candidate count. >= 5 repeats, the arms rotate
// order every round so no size is measured on a systematically colder or warmer store than its
// neighbour, the loaded assembly is sha-stamped because a number must trace back to a binary, and the
// pre-flight refuses the run when a candidate is not content-fresh — a green number on a dirty set
// would be measuring an inline refresh, not the read path.

var label = ArgVal(args, "--label") ?? "unlabelled";
var root = ArgVal(args, "--root") ?? throw new InvalidOperationException("--root is required");
var reps = int.Parse(ArgVal(args, "--reps") ?? "7");
var auditSize = int.Parse(ArgVal(args, "--audit") ?? "200");
var sizes = new[] { 1, 10, 50, 100, 200 };

var rows = new List<Dictionary<string, object>>();

void Report(string arm, double median, double min, double max, int count, string note = "")
{
    rows.Add(new Dictionary<string, object>
    {
        ["arm"] = arm, ["ms"] = Math.Round(median, 2),
        ["min"] = Math.Round(min, 2), ["max"] = Math.Round(max, 2),
        ["reps"] = count, ["note"] = note, ["label"] = label,
    });
    Console.WriteLine($"{arm,-40} med {median,9:F2} ms  min {min,8:F2}  max {max,9:F2}  n={count}  {note}");
}

static string ArgVal(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }
    return null;
}

var manifest = WorkspaceManifestFile.Load(root);
var repo = manifest.Repos[0];
var repoRoot = NavigationContext.ResolveRepoRoot(root, repo);

uint repoId;
IReadOnlyList<FileDocument> all;
int symbolRows;
using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
{
    repoId = store.FindRepo(repo.Name)!.Id;
    all = store.ListFiles(repoId);
    symbolRows = store.ListSymbolsByRepo(repoId).Count;
}

Report("store: density (file rows)", all.Count, all.Count, all.Count, 1,
    $"{symbolRows} symbol rows, {(double)symbolRows / Math.Max(1, all.Count):F1} per file");

var coreAssembly = typeof(FreshnessGuard).Assembly;
Report("build: Squeezr.Core under test", 0, 0, 0, 1,
    $"{coreAssembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(coreAssembly.Location)))[..12]}");

var audited = all.Take(Math.Min(auditSize, all.Count)).ToList();
var notFresh = audited.Where(f => !RowMatchesDisk(repoRoot, f)).ToList();
Report("preflight: fresh candidates (count)", audited.Count - notFresh.Count, audited.Count - notFresh.Count,
    audited.Count - notFresh.Count, 1, $"of {audited.Count} in the measured set");
foreach (var offender in notFresh.Take(5))
{
    Console.WriteLine($"  NOT FRESH: {offender.Path}");
}

if (notFresh.Count > 0)
{
    Console.WriteLine("PREFLIGHT_FAILED — the check-path arms would measure refresh instead");
    Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    Environment.Exit(2);
}

Measure("store: ListFiles(all rows)", () =>
{
    using var s = new MetaStore(SqueezrPaths.MetaDbPath(root));
    var _ = s.ListFiles(repoId);
}, $"{all.Count} file rows");

Measure("store: open+ctor only", () =>
{
    using var s = new MetaStore(SqueezrPaths.MetaDbPath(root));
});

Measure("store: 50 opens (guard shape)", () =>
{
    for (var i = 0; i < 50; i++)
    {
        using var s = new MetaStore(SqueezrPaths.MetaDbPath(root));
    }
}, "50x ctor");

Measure("store: FindFile x50 (indexed)", () =>
{
    using var s = new MetaStore(SqueezrPaths.MetaDbPath(root));
    for (var i = 0; i < 50; i++)
    {
        s.FindFile(repoId, all[i].Path);
    }
});

var shaPaths = Enumerable.Range(0, 50).Select(i => Path.Combine(repoRoot, all[i].Path)).ToList();
Measure("disk: stat x50", () =>
{
    foreach (var p in shaPaths)
    {
        var _ = new FileInfo(p).Length;
    }
});

Measure("disk: sha256 x50 (read all bytes)", () =>
{
    foreach (var p in shaPaths)
    {
        SHA256.HashData(File.ReadAllBytes(p));
    }
});

var candidates = sizes.ToDictionary(n => n, n => all.Take(n).Select(f => (repo.Name, f.Path)).ToList());

var samples = sizes.ToDictionary(n => n, _ => new List<double>());
for (var round = 0; round < reps; round++)
{
    for (var i = 0; i < sizes.Length; i++)
    {
        var n = sizes[(i + round) % sizes.Length];
        var sw = Stopwatch.GetTimestamp();
        FreshnessGuard.CheckAndRefresh(root, candidates[n]);
        samples[n].Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
    }
}

foreach (var n in sizes)
{
    var sorted = samples[n].Order().ToList();
    Report($"guard CheckAndRefresh n={n} (fresh)", sorted[sorted.Count / 2], sorted[0], sorted[^1], sorted.Count,
        $"{n} candidates, arms rotated");
}

var capFifty = samples[50];
var capMedian = capFifty.Order().ElementAt(capFifty.Count / 2);
Report("derived: per-candidate at cap 50", capMedian / 50, capFifty.Min() / 50, capFifty.Max() / 50, capFifty.Count,
    $"{capMedian:F1}ms / 50");

Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string arm, Action act, string note = "")
{
    var batch = new List<double>();
    for (var i = 0; i < reps; i++)
    {
        var sw = Stopwatch.GetTimestamp();
        act();
        batch.Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
    }
    batch.Sort();
    Report(arm, batch[batch.Count / 2], batch[0], batch[^1], batch.Count, note);
}

static bool RowMatchesDisk(string repoRoot, FileDocument doc)
{
    var fullPath = Path.Combine(repoRoot, doc.Path.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(fullPath)
        && UnixFileType.IsRegularFile(fullPath)
        && new FileInfo(fullPath).Length == doc.SizeBytes
        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant() == doc.Sha256;
}
GUARDEOF

  cat > "$PROBES/StructureProbe/StructureProbe.cs" <<'STRUCTEOF'
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

// Prices the single structural pass repo_map pays, against the map's own render and against the
// content guard's cap-50 read path, on the same stand in the same process. The verdict counts travel
// with the number — a pass over a stand whose rows all moved measures a different branch than the
// steady-state answer pays.

var label = ArgVal(args, "--label") ?? "unlabelled";
var root = ArgVal(args, "--root") ?? throw new InvalidOperationException("--root is required");
var reps = int.Parse(ArgVal(args, "--reps") ?? "7");

var rows = new List<Dictionary<string, object>>();
var samplesByArm = new Dictionary<string, List<double>>(StringComparer.Ordinal);

static string ArgVal(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }
    return null;
}

static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

static string DiskPath(string repoRoot, string relativePath) =>
    Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

static string Sha(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

var manifest = WorkspaceManifestFile.Load(root);
var repo = manifest.Repos[0];
var repoRoot = NavigationContext.ResolveRepoRoot(root, repo);

uint repoId;
IReadOnlyList<FileDocument> all;
int symbolRows;
using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
{
    repoId = store.FindRepo(repo.Name)!.Id;
    all = store.ListFiles(repoId);
    symbolRows = store.ListSymbolsByRepo(repoId).Count;
}

Report("stand: file rows in scope",
    $"{all.Count} rows, {symbolRows} symbol rows, {(double)symbolRows / Math.Max(1, all.Count):F1} per file");

var corePath = typeof(StructureFreshnessCheck).Assembly.Location;
Report("build: Squeezr.Core under test",
    $"{corePath} sha256={Sha(corePath)[..12]}");

var probe = all[0];
Report("stamp: row IndexedAt frame",
    $"stored={probe.IndexedAt:O} kind={probe.IndexedAt.Kind} asUtc={ToUtc(probe.IndexedAt):O} " +
    $"diskMtimeUtc={File.GetLastWriteTimeUtc(DiskPath(repoRoot, probe.Path)):O}");

var survey = StructureFreshnessCheck.Check(root, [repo.Name], null);
Report("pass: coverage of this run",
    $"checked={survey.Checked} missing={survey.Missing.Count} changed={survey.Changed.Count} " +
    $"withheld={survey.Withheld} skipped={survey.Skipped}");
foreach (var offender in survey.Missing.Take(3).Concat(survey.Changed.Take(3)))
{
    Console.WriteLine($"  VERDICT: {offender}");
}

var shaByPath = all.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
var guardCandidates = all.Take(50).Select(f => (repo.Name, f.Path)).ToList();
var notContentFresh = guardCandidates.Count(c => !ContentMatchesDisk(c.Path));
Console.WriteLine($"preflight: guard arm candidates content-fresh = {notContentFresh == 0} " +
    $"({guardCandidates.Count - notContentFresh}/{guardCandidates.Count})");

bool ContentMatchesDisk(string relativePath)
{
    var path = DiskPath(repoRoot, relativePath);
    return File.Exists(path) && UnixFileType.IsRegularFile(path) && Sha(path) == shaByPath[relativePath];
}

var timedArms = new List<(string Name, Action Act, string Note)>
{
    ("map: RepoMapper.Build render only",
        () => new RepoMapper(root).Build(null, RepoMapper.DefaultDepth, RepoMapper.DefaultMaxTokens, null),
        $"depth {RepoMapper.DefaultDepth}, the price the map already paid before this task"),
    ($"pass: StructureFreshnessCheck x{survey.Checked} rows",
        () => StructureFreshnessCheck.Check(root, [repo.Name], null),
        "one store session, one stat per row, no content read, no store write"),
};

if (notContentFresh == 0)
{
    timedArms.Add(("guard: CheckAndRefresh cap 50 (post-T1)",
        () => FreshnessGuard.CheckAndRefresh(root, guardCandidates),
        "the capped content path the map used before this task"));
}
else
{
    Console.WriteLine("guard arm SKIPPED — the stand is not content-fresh, a cap-50 number here would measure refresh");
}

for (var round = 0; round < reps; round++)
{
    for (var i = 0; i < timedArms.Count; i++)
    {
        var arm = timedArms[(i + round) % timedArms.Count];
        Time(arm.Name, arm.Act);
    }
}

foreach (var arm in timedArms)
{
    Report(arm.Name, arm.Note);
}

var components = new (string Name, Action Act, string Note)[]
{
    ("component: store open only",
        () => { using var _ = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(root)); },
        "LiteDB open + EnsureCoreIndexes"),
    ("component: ListFiles(all rows)",
        () =>
        {
            using var s = new MetaStore(SqueezrPaths.MetaDbPath(root));
            var _ = s.ListFiles(repoId);
        },
        $"{all.Count} rows materialised in one session"),
    ("component: stat every row",
        () =>
        {
            foreach (var doc in all)
            {
                var info = new FileInfo(DiskPath(repoRoot, doc.Path));
                var _ = info.Exists ? info.Length : 0;
            }
        },
        $"{all.Count} FileInfo probes — the pass adds a file-type probe per row to this"),
};

foreach (var (name, act, note) in components)
{
    for (var round = 0; round < reps; round++)
    {
        Time(name, act);
    }
    Report(name, note);
}

Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

void Time(string arm, Action act)
{
    if (!samplesByArm.TryGetValue(arm, out var samples))
    {
        samplesByArm[arm] = samples = [];
    }
    var sw = Stopwatch.GetTimestamp();
    act();
    samples.Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
}

void Report(string arm, string note)
{
    if (!samplesByArm.TryGetValue(arm, out var samples))
    {
        rows.Add(new Dictionary<string, object> { ["arm"] = arm, ["note"] = note, ["label"] = label });
        Console.WriteLine($"{arm,-44} {note}");
        return;
    }
    var sorted = samples.Order().ToList();
    var median = sorted[sorted.Count / 2];
    rows.Add(new Dictionary<string, object>
    {
        ["arm"] = arm,
        ["ms"] = Math.Round(median, 2),
        ["min"] = Math.Round(sorted[0], 2),
        ["max"] = Math.Round(sorted[^1], 2),
        ["reps"] = sorted.Count,
        ["note"] = note,
        ["label"] = label,
    });
    Console.WriteLine($"{arm,-44} med {median,9:F2} ms  min {sorted[0],8:F2}  max {sorted[^1],9:F2}  " +
        $"n={sorted.Count}  {note}");
}
STRUCTEOF

  cat > "$PROBES/EquivProbe/EquivProbe.cs" <<'EQUIVEOF'
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

// The guard's row lookup moved from ListFiles(repoId) + an Ordinal filter to the indexed
// MetaStore.FindFile(repoId, path). This walks every row of a stand and asserts the two routes name the
// same row, so the swap is a measured equivalence on real data rather than an argument about collation.

var root = args.Length > 0 ? args[0] : throw new InvalidOperationException("stand root is required");
var manifest = WorkspaceManifestFile.Load(root);
var repo = manifest.Repos[0];

using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
var repoId = store.FindRepo(repo.Name)!.Id;
var rows = store.ListFiles(repoId);

var mismatches = new List<string>();
var lookedUp = 0;
foreach (var row in rows)
{
    var hit = store.FindFile(repoId, row.Path);
    lookedUp++;
    if (hit is null || hit.Id != row.Id || hit.Sha256 != row.Sha256 || hit.SizeBytes != row.SizeBytes)
    {
        mismatches.Add($"{row.Path} -> {(hit is null ? "no row" : $"row {hit.Id} sha {hit.Sha256?[..8]}")}, expected {row.Id} sha {row.Sha256?[..8]}");
    }
}

Console.WriteLine($"stand={root} rows={rows.Count} looked_up={lookedUp} mismatches={mismatches.Count}");
foreach (var line in mismatches.Take(10))
{
    Console.WriteLine($"  MISMATCH {line}");
}

var sample = rows[0].Path;
var swapped = char.IsUpper(sample[0]) ? char.ToLowerInvariant(sample[0]) + sample[1..] : char.ToUpperInvariant(sample[0]) + sample[1..];
Console.WriteLine($"collation: FindFile('{swapped}') returned {(store.FindFile(repoId, swapped) is null ? "null (case-sensitive)" : "a row (case-insensitive)")}");
Console.WriteLine($"collation: File.Exists for the case-swapped path on disk: {File.Exists(Path.Combine(NavigationContext.ResolveRepoRoot(root, repo), swapped))}");
return mismatches.Count == 0 ? 0 : 1;
EQUIVEOF

  cat > "$PROBES/StandAudit/StandAudit.cs" <<'AUDITEOF'
using System.Security.Cryptography;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

// sha+size of every indexed row against the disk — the content half of the stand-integrity check the
// store-hash verification cannot make (a store can hash-match a disk it no longer describes).

var root = args.Length > 0 ? args[0] : throw new InvalidOperationException("stand root is required");
var manifest = WorkspaceManifestFile.Load(root);
var repo = manifest.Repos[0];
var repoRoot = NavigationContext.ResolveRepoRoot(root, repo);

List<FileDocument> rows;
using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
{
    rows = [.. store.ListFiles(store.FindRepo(repo.Name)!.Id)];
}

var mismatched = new List<string>();
foreach (var row in rows)
{
    var full = Path.Combine(repoRoot, row.Path.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(full))
    {
        mismatched.Add($"{row.Path} missing");
        continue;
    }
    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
    var size = new FileInfo(full).Length;
    if (sha != row.Sha256 || size != row.SizeBytes)
    {
        mismatched.Add($"{row.Path} sha={(sha == row.Sha256 ? "match" : "DIFFER")} size={size}/{row.SizeBytes}");
    }
}

Console.WriteLine($"AUDIT root={root} rows={rows.Count} contentMismatch={mismatched.Count}");
foreach (var line in mismatched.Take(30))
{
    Console.WriteLine($"  {line}");
}
AUDITEOF

  cat > "$PROBES/stdio-arm.py" <<'PYEOF'
#!/usr/bin/env python3
"""017 budget-wire arm: one serve process, real MCP stdio, raw bytes parsed.

Each arm points a squeezed serve build at the real stand with SQUEEZR_HOME pinned to the stand's own
scratch home (the live ~/.squeezr is never opened). The response cache is in-process and identical
repeat calls would be served in ~2 ms, so every rep gets its own cold process and arguments vary.
"""
import json
import math
import os
import subprocess
import sys
import threading
import time
from queue import Queue, Empty

PROTO = "2024-11-05"


def wire_tokens(text):
    # the product's own basis: every quote that travels inside the MCP content string costs six bytes,
    # so five extra per quote, ceil(len/4*1.1) on the result, minimum one
    return max(1, math.ceil((len(text) + 5 * text.count('"')) / 4 * 1.1))


class Serve:
    def __init__(self, dll, root, home, client="bench-017"):
        env = dict(os.environ, SQUEEZR_HOME=home, DOTNET_CLI_TELEMETRY_OPTOUT="1")
        self.p = subprocess.Popen(
            ["dotnet", dll, "serve"], cwd=root, env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, bufsize=1)
        self.q = Queue()
        self.stderr_lines = []
        threading.Thread(target=self._reader, daemon=True).start()
        threading.Thread(target=self._err, daemon=True).start()
        self._rpc("initialize", {
            "protocolVersion": PROTO, "capabilities": {},
            "clientInfo": {"name": client, "version": "0"}}, id=1, timeout=120)
        self._notify("notifications/initialized")

    def _reader(self):
        for line in self.p.stdout:
            line = line.strip()
            if line:
                self.q.put(line)

    def _err(self):
        for line in self.p.stderr:
            self.stderr_lines.append(line.rstrip())

    def _send(self, obj):
        self.p.stdin.write(json.dumps(obj) + "\n")
        self.p.stdin.flush()

    def _notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})

    def _rpc(self, method, params, id, timeout=300):
        self._send({"jsonrpc": "2.0", "id": id, "method": method, "params": params})
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                line = self.q.get(timeout=deadline - time.time())
            except Empty:
                break
            msg = json.loads(line)
            if msg.get("id") == id:
                return line, msg
        raise RuntimeError(f"no response to {method} (stderr tail: {self.stderr_lines[-5:]})")

    def call(self, tool, arguments, timeout=600):
        started = time.monotonic()
        line, msg = self._rpc("tools/call", {"name": tool, "arguments": arguments},
                              id=int(time.time() * 1000) % 100000, timeout=timeout)
        ms = (time.monotonic() - started) * 1000
        text = msg["result"]["content"][0]["text"]
        return {"wall_ms": ms, "wire_bytes": len(line), "raw": line, "text": text,
                "text_bytes": len(text), "answer": json.loads(text)}

    def close(self):
        try:
            self.p.stdin.close()
        except (OSError, ValueError):
            pass
        self.p.terminate()
        try:
            self.p.wait(20)
        except subprocess.TimeoutExpired:
            self.p.kill()


def report(cfg, raw_dir=None):
    s = Serve(cfg["dll"], cfg["root"], cfg["home"], client=cfg["label"])
    try:
        res = s.call("code_search", cfg["arguments"])
    finally:
        s.close()
    a = res["answer"]
    if raw_dir:
        os.makedirs(raw_dir, exist_ok=True)
        with open(os.path.join(raw_dir, cfg["label"] + "-wire.json"), "w") as fh:
            fh.write(res["raw"])
        with open(os.path.join(raw_dir, cfg["label"] + "-answer.json"), "w") as fh:
            fh.write(res["text"])
    f = a.get("freshness") or {}
    out = {
        "arm": cfg["label"],
        "binary": os.path.basename(cfg["dll"]) + "@" + os.path.basename(os.path.dirname(cfg["dll"])),
        "dll_sha12": cfg.get("dll_sha12", ""),
        "args": cfg["arguments"],
        "wall_ms": round(res["wall_ms"], 1),
        "wire_bytes": res["wire_bytes"],
        "text_bytes": res["text_bytes"],
        "tokensUsed": a.get("tokensUsed"),
        "truncated": a.get("truncated"),
        "staleFiles": len(a.get("staleFiles") or []),
        "warnings": len(a.get("warnings") or []),
        "budget_line": next((w for w in (a.get("warnings") or []) if w.startswith("refresh budget")), None),
        "freshness": f,
        "paid_tokens": (a.get("tokensUsed") or 0) + (f.get("freshnessTokens") or 0),
        "block_wire_tokens": wire_tokens(json.dumps(f)),
    }
    print("ARM " + json.dumps(out, ensure_ascii=False))
    return out


if __name__ == "__main__":
    cfg = json.load(open(sys.argv[1]))
    report(cfg, os.environ.get("BENCH_RAW_DIR"))
PYEOF

  cat > "$PROBES/dirty.py" <<'DIRTYEOF'
#!/usr/bin/env python3
"""017 wire-arm dirty/restore: files named by the answer itself, marker appended, then stripped back.

Capture records sha+size+mtime_ns per named file. Dirty appends one marker line. Restore strips exact
marker repetitions, puts the recorded ns stamp back with os.utime (nanoseconds — a seconds-vs-ns bug
once left 24 stand files with junk mtime) and re-verifies sha+size against the record: DRIFT is printed
per file that came back different, so an arm measured on residue is visible in the log.
"""
import hashlib
import json
import os
import sys


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def record(names, root, out):
    files = []
    for rel in names:
        p = os.path.join(root, rel)
        st = os.stat(p)
        files.append({"path": rel, "sha": sha(p), "size": st.st_size, "mtimeNs": int(st.st_mtime_ns)})
    json.dump({"root": root, "files": files}, open(out, "w"), indent=1)
    print(f"CAPTURED files={len(files)} -> {out}")


def dirty(root, named_file, count, mark, ds):
    named = json.load(open(named_file))["files"]
    picked = named[:count]
    for row in picked:
        with open(os.path.join(root, row["path"]), "a") as fh:
            fh.write(mark + "\n")
    rels = [row["path"] for row in picked]
    json.dump({"files": rels, "mark": mark}, open(ds, "w"))
    print(f"DIRTY n={len(rels)} mark={mark}")


def restore(root, named_file, ds):
    state = json.load(open(named_file))
    by_path = {f["path"]: f for f in state["files"]}
    drift = 0
    if not os.path.exists(ds):
        print("RESTORE no dirty-set (nothing dirtied since capture)")
        return
    dstate = json.load(open(ds))
    mark = (dstate["mark"] + "\n").encode()
    for rel in dstate["files"]:
        path = os.path.join(root, rel)
        data = open(path, "rb").read()
        while data.endswith(mark):
            data = data[:-len(mark)]
        with open(path, "wb") as fh:
            fh.write(data)
        want = by_path[rel]
        os.utime(path, ns=(want["mtimeNs"], want["mtimeNs"]))
        ok = sha(path) == want["sha"] and os.path.getsize(path) == want["size"]
        if not ok:
            drift += 1
            print(f"DRIFT {rel} restored=0")
    print(f"RESTORED drift={drift}")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "record":
        record(json.load(open(sys.argv[2]))["files"], sys.argv[3], sys.argv[4])
    elif cmd == "dirty":
        dirty(sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5], sys.argv[6])
    elif cmd == "restore":
        restore(sys.argv[2], sys.argv[3], sys.argv[4])
DIRTYEOF

  cat > "$PROBES/capture.py" <<'CAPEOF'
#!/usr/bin/env python3
"""Wire-scenario capture: the files the after carrier's own answer names on a clean store.

The dirty set for every later arm is exactly the first N of this list — the answer is re-read against
files it itself calls, so candidates are inside the capped set by construction, and both carriers see
one identical list."""
import json
import os
import sys
from importlib.machinery import SourceFileLoader

arm = SourceFileLoader("arm", os.environ["BENCH_STDIO_ARM"]).load_module()
root = sys.argv[1]
home = sys.argv[2]
dll = sys.argv[3]
out = sys.argv[4]
s = arm.Serve(dll, root, home, client="bench-017-capture")
try:
    res = s.call("code_search", {"query": "Memory", "topK": 60, "maxTokens": 12000})
finally:
    s.close()
data = res["answer"].get("data") or []
names, seen = [], set()
for hit in data:
    file = hit["file"]
    if file not in seen:
        seen.add(file)
        names.append(file)
if not names:
    raise SystemExit("CAPTURE-FAILED the clean answer named no files")
json.dump({"root": root, "files": names,
           "wall_ms": round(res["wall_ms"], 1), "rows": len(data)}, open(out, "w"), indent=1)
print(f"CAPTURE files={len(names)} rows={len(data)} wall={res['wall_ms']:.0f}ms")
CAPEOF
}

build_probe() {
  local entry=$1 core=$2 name=$3
  local dir="$SCRATCH/probe-builds/$name"
  rm -rf "$dir"
  mkdir -p "$dir"
  cp "$PROBES/$entry/$entry.cs" "$dir/"
  sed -e "s|{ENTRY}|$entry|" -e "s|{CORE}|$core|" "$PROBES/probe.csproj.tmpl" > "$dir/$entry.csproj"
  say "$dir"
}

run_probe() {
  local name=$1 entry=$2 core=$3 stand=$4 batch=$5 first_tag=$6
  local dir
  dir=$(build_probe "$entry" "$core" "$name")
  local head
  head=$(git -C "$BENCH_REPO" rev-parse --short HEAD)
  local log="$LOGS/$name.log"
  say "=== arm $name  stand=$stand  batch=$batch  head=$head  load=$(load1)"
  SQUEEZR_HOME="$STAND_HOME" DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    dotnet run --project "$dir" -- \
    --label "$name head=$head" --root "$stand" --reps "$REPS" > "$log" 2>&1
  local ec=$?
  echo "exit=$ec" >> "$log"
  grep -E "^(guard|pass|map|stand|build|derived|preflight|component|NOT FRESH|PREFLIGHT|  VERDICT|.*med )" "$log" | head -30
  return $ec
}

cmd_guard() {
  local batch=${1:?usage: guard <1|2>}
  mkdir -p "$LOGS"
  local order
  case "$batch" in
    1) order="before after" ;;
    2) order="after before" ;;
    *) die "batch is 1 or 2" ;;
  esac
  local build side pair tag root
  for build in $order; do
    case "$build" in before) side="$CARRIERS/before" ;; *) side="$CARRIERS/after" ;; esac
    for pair in "12k:$POLY_ROOT" "ctrl:$CONTROL_ROOT" "real:$REAL_ROOT"; do
      tag=${pair%%:*}
      root=${pair#*:}
      run_probe "guard-$build-$tag-b$batch" GuardProbe "$side/src/Squeezr.Core/Squeezr.Core.csproj" \
        "$root" "$batch" "$build" || say "ARM_FAILED guard-$build-$tag-b$batch"
    done
  done
}

cmd_structure() {
  local batch=${1:?usage: structure <1|2>}
  mkdir -p "$LOGS"
  local pair tag root core="$CARRIERS/after/src/Squeezr.Core/Squeezr.Core.csproj"
  case "$batch" in
    1) list="12k:$POLY_ROOT real:$REAL_ROOT ctrl:$CONTROL_ROOT" ;;
    2) list="ctrl:$CONTROL_ROOT real:$REAL_ROOT 12k:$POLY_ROOT" ;;
    *) die "batch is 1 or 2" ;;
  esac
  for pair in $list; do
    tag=${pair%%:*}
    root=${pair#*:}
    run_probe "struct-$tag-b$batch" StructureProbe "$core" "$root" "$batch" after \
      || say "ARM_FAILED struct-$tag-b$batch"
  done
}

cmd_equiv() {
  local stand=${1:?usage: equiv <stand-root>}
  local dir
  dir=$(build_probe EquivProbe "$CARRIERS/after/src/Squeezr.Core/Squeezr.Core.csproj" "equiv")
  SQUEEZR_HOME="$STAND_HOME" dotnet run --project "$dir" -- "$stand" 2>&1 | grep -E "^(stand=|collation|  MISMATCH)"
}

cmd_stands() {
  mkdir -p "$SCRATCH/stands" "$STAND_HOME"
  local squeezr="$AFTER_DLL"
  [ -f "$squeezr" ] || die "prepare first (serve carrier $squeezr missing)"

  make_poly() {
    local root=$1 dirs=$2 name=$3
    mkdir -p "$root"
    ( cd "$root" && GEN_ROOT="$root/polygon" GEN_DIRS="$dirs" GEN_FILES="$GEN_FILES" \
        python3 "$PROBES/gen.py" && \
      SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" init --name "$name" && \
      SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" add polygon --name poly && \
      SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" index --no-semantic )
    snapshot_store "$root"
  }

  [ -d "$POLY_ROOT/polygon" ] && say "poly exists: $POLY_ROOT" || { say "generating poly-12k"; make_poly "$POLY_ROOT" "$GEN_DIRS" bench-poly-12k; }
  [ -d "$CONTROL_ROOT/polygon" ] && say "control exists: $CONTROL_ROOT" || { say "generating poly-1.2k"; make_poly "$CONTROL_ROOT" "$GEN_CTRL_DIRS" bench-poly-1200; }

  if [ -d "$REAL_ROOT/polygon" ]; then
    say "real exists: $REAL_ROOT"
  else
    [ -n "$REAL_SOURCE" ] || die "real stand missing and REAL_SOURCE not set (git repo to archive from)"
    mkdir -p "$REAL_ROOT"
    mkdir -p "$REAL_ROOT/polygon"
    git -C "$REAL_SOURCE" archive "$REAL_REF" | tar -x -C "$REAL_ROOT/polygon"
    ( cd "$REAL_ROOT" && SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" init --name bench-real && \
      SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" add polygon --name poly && \
      SQUEEZR_HOME="$STAND_HOME" dotnet "$squeezr" index --no-semantic )
    snapshot_store "$REAL_ROOT"
  fi
}

snapshot_store() {
  local root=$1 key
  key=$(store_key "$root") || die "stand $root not registered in $STAND_HOME"
  mkdir -p "$PRISTINE_DIR/$key"
  local f present=()
  for f in $STORE_FILES; do
    [ -f "$STAND_HOME/workspaces/$key/$f" ] || continue
    cp "$STAND_HOME/workspaces/$key/$f" "$PRISTINE_DIR/$key/$f"
    touch -r "$STAND_HOME/workspaces/$key/$f" "$PRISTINE_DIR/$key/$f"
    present+=("$key/$f")
  done
  # the .sha lists paths relative to the workspaces directory — verify_store checks from inside it
  (cd "$STAND_HOME/workspaces" && sha256sum "${present[@]}" > "$PRISTINE_DIR/$key.sha")
  say "SNAPSHOT $key -> $PRISTINE_DIR"
}

cmd_wire_capture() {
  mkdir -p "$LOGS"
  verify_store "$REAL_ROOT" || die "real store is not pristine — run verify/restore before capture"
  SQUEEZR_HOME="$STAND_HOME" BENCH_STDIO_ARM="$PROBES/stdio-arm.py" \
    python3 "$PROBES/capture.py" "$REAL_ROOT" "$STAND_HOME" "$AFTER_DLL" "$SCRATCH/names.json" \
    > "$LOGS/wire-capture.log" 2>&1
  local ec=$?
  tail -2 "$LOGS/wire-capture.log"
  [ $ec -eq 0 ] || die "capture failed, see logs/wire-capture.log"
  python3 "$PROBES/dirty.py" record "$SCRATCH/names.json" "$REAL_ROOT/polygon" "$NAMED" >> "$LOGS/wire-capture.log" 2>&1
  restore_store "$REAL_ROOT"
  verify_store "$REAL_ROOT"
  local n
  n=$(python3 -c 'import json;print(len(json.load(open("'"$NAMED"'"))["files"]))')
  say "CAPTURE-OK named=$n (dirty counts: $DIRTY_COUNTS must be <= $n)"
}

wire_arm() {
  local build=$1 count=$2 rep=$3 batch=$4
  local dll side label
  case "$build" in
    prebudget) side="$CARRIERS/prebudget" ;;
    *) side="$CARRIERS/after" ;;
  esac
  dll="$side/src/Squeezr.Cli/bin/Release/net10.0/squeezr.dll"
  [ -f "$dll" ] || die "missing Release serve dll $dll (prepare)"
  label="wire-$build-$count-r$rep-b$batch"
  local sha12
  sha12=$(sha256sum "$dll" | cut -c1-12)
  restore_store "$REAL_ROOT"
  python3 "$PROBES/dirty.py" dirty "$REAL_ROOT/polygon" "$NAMED" "$count" "t5b$batch-$count-r$rep" \
    "$SCRATCH/dirty-set.json" > "$LOGS/$label.log" 2>&1
  cat > "$SCRATCH/arm-$label.json" <<JSON
{"label":"$label","dll":"$dll","dll_sha12":"$sha12","root":"$REAL_ROOT","home":"$STAND_HOME",
 "arguments":{"query":"Memory","topK":60,"maxTokens":$((11999 + count))}}
JSON
  say "--- arm $label dll_sha12=$sha12 load=$(load1)"
  BENCH_RAW_DIR="$SCRATCH/raw" python3 -u "$PROBES/stdio-arm.py" "$SCRATCH/arm-$label.json" >> "$LOGS/$label.log" 2>&1
  local ec=$?
  grep -o 'ARM .*' "$LOGS/$label.log" | tail -1
  python3 "$PROBES/dirty.py" restore "$REAL_ROOT/polygon" "$NAMED" "$SCRATCH/dirty-set.json" >> "$LOGS/$label.log" 2>&1
  grep -E "^(RESTORED|DRIFT)" "$LOGS/$label.log"
  rm -f "$SCRATCH/dirty-set.json"
  restore_store "$REAL_ROOT"
  return $ec
}

cmd_wire() {
  local batch=${1:?usage: wire <1|2>}
  [ -f "$NAMED" ] || die "run wire-capture first"
  mkdir -p "$LOGS" "$SCRATCH/raw"
  local count build order
  case "$batch" in
    1) order="prebudget after" ;;
    2) order="after prebudget" ;;
    *) die "batch is 1 or 2" ;;
  esac
  for count in $DIRTY_COUNTS; do
    for build in $order; do
      local rep
      for rep in $(seq 1 "$WIRE_REPS"); do
        wire_arm "$build" "$count" "$rep" "$batch" || say "ARM_FAILED wire-$build-$count-r$rep-b$batch"
      done
    done
  done
  verify_store "$REAL_ROOT"
  cmd_audit
}

cmd_audit() {
  local dir
  dir=$(build_probe StandAudit "$CARRIERS/after/src/Squeezr.Core/Squeezr.Core.csproj" "audit")
  local stand
  for stand in "$REAL_ROOT" "$POLY_ROOT" "$CONTROL_ROOT"; do
    [ -d "$stand/polygon" ] || continue
    SQUEEZR_HOME="$STAND_HOME" dotnet run --project "$dir" -- "$stand" 2>&1 | grep -E "^(AUDIT|  )"
  done
  say "=== structure pass over every row (after binary)"
  local sd
  sd=$(build_probe StructureProbe "$CARRIERS/after/src/Squeezr.Core/Squeezr.Core.csproj" "verify-struct")
  SQUEEZR_HOME="$STAND_HOME" REPS=1 \
    dotnet run --project "$sd" -- --root "$REAL_ROOT" --reps 1 --label verify 2>&1 | grep -E "pass: coverage|VERDICT"
}

cmd_verify() {
  verify_store "$REAL_ROOT"
  verify_store "$POLY_ROOT"
  verify_store "$CONTROL_ROOT"
  cmd_audit
}

cmd_cleanup() {
  rm -rf "$SCRATCH"/home-* "$SCRATCH"/probe-builds "$SCRATCH"/arm-wire-*.json 2>/dev/null
  say "CLEANUP done (carriers, probes, logs, named kept under $SCRATCH; stands untouched)"
}

case "${1:-plan}" in
  --dry-run|plan) cmd_plan ;;
  prepare) cmd_prepare ;;
  stands) cmd_stands ;;
  guard) shift; cmd_guard "$@" ;;
  structure) shift; cmd_structure "$@" ;;
  equiv) shift; cmd_equiv "$@" ;;
  wire-capture) cmd_wire_capture ;;
  wire) shift; cmd_wire "$@" ;;
  verify) cmd_verify ;;
  audit) cmd_audit ;;
  cleanup) cmd_cleanup ;;
  *) die "unknown command: $1 (see usage header)" ;;
esac
