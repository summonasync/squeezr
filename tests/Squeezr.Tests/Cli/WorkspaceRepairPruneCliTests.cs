using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceRepairPruneCliTests
{
    private const string NonAsciiName = "проект-проект";

    [Fact]
    public async Task DryRun_ListsStaleRowsWithSizes_ChangesNothing()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var noStoreRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var goneKey = Key("prune-gone", goneRoot);
            var noStoreKey = Key("prune-nostore", noStoreRoot);
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey), Row("prune-gone", goneRoot, goneKey),
                Row("prune-nostore", noStoreRoot, noStoreKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 480));
            var registryBefore = File.ReadAllText(RegistryPath(home));
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(noStoreRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--dry-run");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain("Dry run: nothing adopted, nothing deleted.");
            run.StdOut.ShouldContain($"2 stale row(s)");
            run.StdOut.ShouldContain($"stale: prune-nostore ({noStoreKey}) root {noStoreRoot} · no store dir");
            StaleLine(run.StdOut, goneKey).ShouldMatch(@"· \d+(\.\d)? (B|KB|MB|GB)$");
            run.StdOut.ShouldNotContain(StoreDir(home, goneKey));
            run.StdOut.ShouldNotContain(StoreDir(home, noStoreKey));
            File.ReadAllText(RegistryPath(home)).ShouldBe(registryBefore);
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeTrue();
            Directory.Exists(StoreDir(home, noStoreKey)).ShouldBeFalse();
        }
        finally
        {
            Clean(home, liveRoot, goneRoot, noStoreRoot);
        }
    }

    [Fact]
    public async Task DryRun_DoesNotAdoptOrphans()
    {
        var home = TestDirs.NewTempDir();
        var orphanRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            File.WriteAllText(RegistryPath(home), "[]");
            var orphanKey = Key("prune-orphan", orphanRoot);
            WriteStoreDir(home, orphanKey, "prune-orphan", orphanRoot, ("meta.db", 10));

            var run = await RunAsync(home, "workspace", "repair", "--dry-run");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain("would adopt");
            run.StdOut.ShouldContain(orphanKey);
            File.ReadAllText(RegistryPath(home)).ShouldBe("[]");
        }
        finally
        {
            Clean(home, orphanRoot);
        }
    }

    [Fact]
    public async Task Yes_PrunesRowAndStoreAndRowOnly_KeepsLiveRow()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var noStoreRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var goneKey = Key("prune-gone", goneRoot);
            var noStoreKey = Key("prune-nostore", noStoreRoot);
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey), Row("prune-gone", goneRoot, goneKey),
                Row("prune-nostore", noStoreRoot, noStoreKey));
            WriteStoreDir(home, liveKey, "prune-live", liveRoot, ("meta.db", 10));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 10));
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(noStoreRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(0);
            var prunedStore = ResultLine(run.StdOut, goneKey);
            prunedStore.ShouldStartWith("  - pruned row + store (");
            prunedStore.ShouldEndWith($"): prune-gone ({goneKey}) — performed: squeezr drop --store-key {goneKey}");
            var prunedRow = ResultLine(run.StdOut, noStoreKey);
            prunedRow.ShouldBe($"  - pruned registry row only (no store dir): prune-nostore ({noStoreKey})" +
                $" — performed: squeezr drop --store-key {noStoreKey}");
            run.StdOut.ShouldContain("Pruned 2 stale row(s)");
            run.StdOut.ShouldNotContain(StoreDir(home, goneKey));
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe([liveKey]);
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeFalse();
            Directory.Exists(StoreDir(home, liveKey)).ShouldBeTrue();
        }
        finally
        {
            Clean(home, liveRoot, goneRoot, noStoreRoot);
        }
    }

    [Fact]
    public async Task Yes_Json_CarriesPerRowRecordsWithTheActionTaken()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var noStoreRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var goneKey = Key("prune-gone", goneRoot);
            var noStoreKey = Key("prune-nostore", noStoreRoot);
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey), Row("prune-gone", goneRoot, goneKey),
                Row("prune-nostore", noStoreRoot, noStoreKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 512));
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(noStoreRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var body = document.RootElement;
            body.GetProperty("prune").GetProperty("mode").GetString().ShouldBe("pruned");
            var stale = body.GetProperty("stale");
            stale.GetArrayLength().ShouldBe(2);
            stale.EnumerateArray().Select(row => row.GetProperty("storeKey").GetString())
                .ShouldBe([goneKey, noStoreKey]);
            stale.EnumerateArray().Single(row => row.GetProperty("storeKey").GetString() == noStoreKey)
                .TryGetProperty("storeBytes", out _).ShouldBeFalse();
            var rows = body.GetProperty("prune").GetProperty("rows");
            rows.GetArrayLength().ShouldBe(2);
            var rowAndStore = rows.EnumerateArray()
                .Single(row => row.GetProperty("storeKey").GetString() == goneKey);
            rowAndStore.GetProperty("action").GetString().ShouldBe("pruned-row-and-store");
            rowAndStore.GetProperty("category").GetString().ShouldBe("stale");
            rowAndStore.GetProperty("rootPath").GetString().ShouldBe(goneRoot);
            rowAndStore.GetProperty("removedPaths").EnumerateArray()
                .Select(path => path.GetString()).ShouldBe([StoreDir(home, goneKey)]);
            rows.EnumerateArray().Single(row => row.GetProperty("storeKey").GetString() == noStoreKey)
                .GetProperty("action").GetString().ShouldBe("pruned-row-only");
            body.GetProperty("prune").GetProperty("totalStoreBytes").GetInt64()
                .ShouldBe(rowAndStore.GetProperty("storeBytes").GetInt64());
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe([liveKey]);
        }
        finally
        {
            Clean(home, liveRoot, goneRoot, noStoreRoot);
        }
    }

    [Fact]
    public async Task NoFlagsNonInteractive_RefusesAndExplains_KeepsEverything()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var goneKey = Key("prune-gone", goneRoot);
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 10));
            var registryBefore = File.ReadAllText(RegistryPath(home));
            Directory.Delete(goneRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldContain("Not pruned: 1 stale row(s) left in the registry.");
            run.StdOut.ShouldContain("Repair report: 0 adopted, 0 need --root, 0 stale row(s) pruned, 0 husk row(s) pruned.");
            run.StdOut.ShouldContain($"stale: prune-gone ({goneKey})");
            run.StdErr.ShouldContain("error: Refusing to prune 1 stale row(s) without confirmation");
            run.StdErr.ShouldContain("this is not an interactive terminal");
            run.StdErr.ShouldContain("the absence of a prompt is not consent");
            run.StdErr.ShouldContain("Re-run with --dry-run to list them, or --yes to confirm");
            run.StdErr.ShouldContain("before a real prune take a dated copy of the registry");
            File.ReadAllText(RegistryPath(home)).ShouldBe(registryBefore);
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeTrue();
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    [Fact]
    public async Task JsonWithoutFlags_RefusesOnErrorBody_StdoutStillCarriesTheReport()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var goneKey = Key("prune-gone", goneRoot);
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey));
            Directory.Delete(goneRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--json");

            run.ExitCode.ShouldBe(1);
            using var report = JsonDocument.Parse(run.StdOut.Trim());
            report.RootElement.GetProperty("prune").GetProperty("mode").GetString().ShouldBe("refused");
            report.RootElement.GetProperty("stale").EnumerateArray()
                .Single(row => row.GetProperty("storeKey").GetString() == goneKey)
                .GetProperty("rootPath").GetString().ShouldBe(goneRoot);
            using var error = JsonDocument.Parse(run.StdErr.Trim());
            error.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_usage");
            error.RootElement.GetProperty("error").GetProperty("message").GetString()!
                .ShouldContain("--json writes one machine-readable document");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(goneKey);
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    [Fact]
    public async Task Yes_AdoptsOrphanAndNeverDropsIt()
    {
        var home = TestDirs.NewTempDir();
        var orphanRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            File.WriteAllText(RegistryPath(home), "[]");
            var orphanKey = Key("prune-orphan", orphanRoot);
            WriteStoreDir(home, orphanKey, "prune-orphan", orphanRoot, ("meta.db", 10));
            Directory.Delete(orphanRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain($"adopted prune-orphan ({orphanKey})");
            run.StdOut.ShouldNotContain($"({orphanKey}) — performed:");
            run.StdOut.ShouldContain("Repair report: 1 adopted, 0 need --root, 0 stale row(s) pruned, 0 husk row(s) pruned.");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(orphanKey);
            Directory.Exists(StoreDir(home, orphanKey)).ShouldBeTrue();
        }
        finally
        {
            Clean(home, orphanRoot);
        }
    }

    [Fact]
    public async Task Yes_PrunesStaleAndHuskRows_BehindSameYes_HomeDirectorySurvives()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        File.WriteAllText(Path.Combine(home, "canary.txt"), "keep me");
        try
        {
            var goneKey = Key("prune-gone", goneRoot);
            var huskKey = Key("prune-husk", Canonical(home));
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey), Row("prune-husk", Canonical(home), huskKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 10));
            WriteStoreDir(home, huskKey, "prune-husk", home, ("meta.db", 10));
            Directory.Delete(goneRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldNotContain("looks like a husk");
            run.StdErr.ShouldNotContain("squeezr drop --store-key");
            ResultLine(run.StdOut, goneKey).ShouldStartWith("  - pruned row + store (");
            ResultLine(run.StdOut, huskKey).ShouldStartWith("  - pruned row + store (");
            run.StdOut.ShouldContain("Pruned 2 row(s) (1 stale, 1 husk) · 2 store dir(s) removed (");
            run.StdOut.ShouldContain("Repair report: 0 adopted, 0 need --root, 1 stale row(s) pruned, 1 husk row(s) pruned.");
            ReadRegistry(home).ShouldBeEmpty();
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeFalse();
            Directory.Exists(StoreDir(home, huskKey)).ShouldBeFalse();
            Directory.Exists(home).ShouldBeTrue();
            File.ReadAllText(Path.Combine(home, "canary.txt")).ShouldBe("keep me");
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    [Fact]
    public async Task DryRun_ListsHuskRowsWithSizes_CountsWouldBePruned_ChangesNothing()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var huskKey = Key("prune-husk", Canonical(home));
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey), Row("prune-husk", Canonical(home), huskKey));
            WriteStoreDir(home, huskKey, "prune-husk", home, ("meta.db", 10));

            var run = await RunAsync(home, "workspace", "repair", "--dry-run");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain("Dry run: nothing adopted, nothing deleted.");
            run.StdErr.ShouldContain("workspace 'prune-husk' looks like a husk");
            run.StdErr.ShouldContain("squeezr drop --store-key");
            run.StdOut.ShouldContain($"husk: prune-husk ({huskKey}) root {Canonical(home)} · ");
            run.StdOut.ShouldContain("Dry run: 1 husk row(s), 1 store dir(s) totalling ");
            run.StdOut.ShouldContain(
                "Repair report: 0 adoptable, 0 need --root, 0 stale row(s) would be pruned, 1 husk row(s) would be pruned.");
            File.ReadAllText(RegistryPath(home)).ShouldContain(huskKey);
            Directory.Exists(StoreDir(home, huskKey)).ShouldBeTrue();
            Directory.Exists(home).ShouldBeTrue();
        }
        finally
        {
            Clean(home, liveRoot);
        }
    }

    [Fact]
    public async Task Yes_AllFourCategories_ExactCounts_OnlyConfirmedDeleted_Json()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var orphanRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var goneKey = Key("prune-gone", goneRoot);
            var huskKey = Key("prune-husk", Canonical(home));
            var orphanKey = Key("prune-orphan", orphanRoot);
            WriteRegistry(home,
                Row("prune-live", liveRoot, liveKey),
                Row("prune-gone", goneRoot, goneKey),
                Row("prune-husk", Canonical(home), huskKey));
            WriteStoreDir(home, liveKey, "prune-live", liveRoot, ("meta.db", 10));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 20));
            WriteStoreDir(home, huskKey, "prune-husk", home, ("meta.db", 30));
            WriteStoreDir(home, orphanKey, "prune-orphan", orphanRoot, ("meta.db", 40));
            Directory.Delete(goneRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var body = document.RootElement;
            body.GetProperty("adopted").EnumerateArray()
                .Select(row => row.GetProperty("storeKey").GetString()).ShouldBe([orphanKey]);
            body.GetProperty("needsRoot").GetArrayLength().ShouldBe(0);
            body.GetProperty("husks").EnumerateArray()
                .Select(row => row.GetProperty("storeKey").GetString()).ShouldBe([huskKey]);
            body.GetProperty("stale").EnumerateArray()
                .Select(row => row.GetProperty("storeKey").GetString()).ShouldBe([goneKey]);
            var rows = body.GetProperty("prune").GetProperty("rows").EnumerateArray().ToList();
            rows.Select(row => row.GetProperty("category").GetString()).ShouldBe(["stale", "husk"]);
            rows.Select(row => row.GetProperty("action").GetString())
                .ShouldBe(["pruned-row-and-store", "pruned-row-and-store"]);
            var counts = body.GetProperty("counts");
            counts.GetProperty("adopted").GetInt32().ShouldBe(1);
            counts.GetProperty("needsRoot").GetInt32().ShouldBe(0);
            counts.GetProperty("stalePruned").GetInt32().ShouldBe(1);
            counts.GetProperty("husksPruned").GetInt32().ShouldBe(1);
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe([liveKey, orphanKey]);
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeFalse();
            Directory.Exists(StoreDir(home, huskKey)).ShouldBeFalse();
            Directory.Exists(StoreDir(home, liveKey)).ShouldBeTrue();
            Directory.Exists(StoreDir(home, orphanKey)).ShouldBeTrue();
            Directory.Exists(home).ShouldBeTrue();
        }
        finally
        {
            Clean(home, liveRoot, goneRoot, orphanRoot);
        }
    }

    [Fact]
    public async Task Yes_DeadRootOrphanStore_AdoptedFirst_SweptAsStaleOnNextRun()
    {
        var home = TestDirs.NewTempDir();
        var deadRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            File.WriteAllText(RegistryPath(home), "[]");
            var deadKey = Key("prune-dead", deadRoot);
            WriteStoreDir(home, deadKey, "prune-dead", deadRoot, ("meta.db", 10));
            Directory.Delete(deadRoot, recursive: true);

            var first = await RunAsync(home, "workspace", "repair", "--yes");

            first.ExitCode.ShouldBe(0);
            first.StdOut.ShouldContain($"adopted prune-dead ({deadKey})");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(deadKey);
            Directory.Exists(StoreDir(home, deadKey)).ShouldBeTrue();

            var second = await RunAsync(home, "workspace", "repair", "--yes");

            second.ExitCode.ShouldBe(0);
            ResultLine(second.StdOut, deadKey).ShouldStartWith("  - pruned row + store (");
            second.StdOut.ShouldContain(
                "Repair report: 0 adopted, 0 need --root, 1 stale row(s) pruned, 0 husk row(s) pruned.");
            ReadRegistry(home).ShouldBeEmpty();
            Directory.Exists(StoreDir(home, deadKey)).ShouldBeFalse();
        }
        finally
        {
            Clean(home, deadRoot);
        }
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--yes")]
    public async Task PruneFlagsWithStoreKey_ReportUsageError(string flag)
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(RegistryPath(home), "[]");
            var key = Key("prune-orphan", Canonical(root));
            WriteStoreDir(home, key, "prune-orphan", root, null);
            string[] args = ["workspace", "repair", flag, "--store-key", key, "--root", root];

            var run = await RunAsync(home, args);

            run.ExitCode.ShouldBe(1);
            run.StdErr.ShouldContain("sweep the whole registry");
            File.ReadAllText(RegistryPath(home)).ShouldBe("[]");
        }
        finally
        {
            Clean(home, root);
        }
    }

    [Fact]
    public async Task DryRunWithYes_ReportsUsageError()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(RegistryPath(home), "[]");

            var run = await RunAsync(home, "workspace", "repair", "--dry-run", "--yes");

            run.ExitCode.ShouldBe(1);
            run.StdErr.ShouldContain("--dry-run and --yes are mutually exclusive");
        }
        finally
        {
            Clean(home);
        }
    }

    [Fact]
    public async Task NothingStale_Yes_PrintsNoPruneSection()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey));

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain("Nothing to repair");
            run.StdOut.ShouldNotContain("pruned");
            run.StdOut.ShouldNotContain("stale row(s)");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(liveKey);
        }
        finally
        {
            Clean(home, liveRoot);
        }
    }

    [Fact]
    public async Task DryRun_Json_CarriesDryRunModeAndWouldPruneActions()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = Canonical(TestDirs.NewTempDir());
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var noStoreRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var liveKey = Key("prune-live", liveRoot);
            var goneKey = Key("prune-gone", goneRoot);
            var noStoreKey = Key("prune-nostore", noStoreRoot);
            WriteRegistry(home, Row("prune-live", liveRoot, liveKey), Row("prune-gone", goneRoot, goneKey),
                Row("prune-nostore", noStoreRoot, noStoreKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 480));
            var goneBytes = StoreBytes(home, goneKey);
            var registryBefore = File.ReadAllText(RegistryPath(home));
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(noStoreRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--dry-run", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var prune = document.RootElement.GetProperty("prune");
            prune.GetProperty("mode").GetString().ShouldBe("dry-run");
            var rows = prune.GetProperty("rows");
            rows.GetArrayLength().ShouldBe(2);
            rows.EnumerateArray().Select(row => row.GetProperty("action").GetString())
                .Distinct().ShouldHaveSingleItem().ShouldBe("would-prune");
            rows.EnumerateArray().Select(row => row.GetProperty("category").GetString())
                .Distinct().ShouldHaveSingleItem().ShouldBe("stale");
            rows.EnumerateArray().ShouldAllBe(row => row.GetProperty("removedPaths").GetArrayLength() == 0);
            prune.GetProperty("totalStoreBytes").GetInt64().ShouldBe(goneBytes);
            prune.GetProperty("storesRemoved").GetInt32().ShouldBe(0);
            StaleRow(document, goneKey).GetProperty("storeState").GetString().ShouldBe("measured");
            StaleRow(document, noStoreKey).GetProperty("storeState").GetString().ShouldBe("missing");
            File.ReadAllText(RegistryPath(home)).ShouldBe(registryBefore);
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeTrue();
        }
        finally
        {
            Clean(home, liveRoot, goneRoot, noStoreRoot);
        }
    }

    [Fact]
    public async Task Yes_NonAsciiStoreKey_PrunsRowAndStore_TheSameWayDropDoes()
    {
        var repairHome = TestDirs.NewTempDir();
        var dropHome = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var goneKey = Key(NonAsciiName, goneRoot);
        try
        {
            goneKey.ShouldStartWith("проект-проект-");
            foreach (var home in new[] { repairHome, dropHome })
            {
                WriteRegistry(home, Row(NonAsciiName, goneRoot, goneKey));
                WriteStoreDir(home, goneKey, NonAsciiName, goneRoot, ("meta.db", 64));
            }
            Directory.Delete(goneRoot, recursive: true);

            var repair = await RunAsync(repairHome, "workspace", "repair", "--yes");
            var drop = await RunAsync(dropHome, "drop", "--store-key", goneKey);

            repair.ExitCode.ShouldBe(0);
            drop.ExitCode.ShouldBe(0);
            ResultLine(repair.StdOut, goneKey).ShouldStartWith("  - pruned row + store (");
            repair.StdOut.ShouldContain("1 store dir(s) removed (");
            repair.StdOut.ShouldNotContain("no store dir");
            repair.StdOut.ShouldNotContain("not a single path segment");
            ReadRegistry(repairHome).ShouldBeEmpty();
            ReadRegistry(dropHome).ShouldBeEmpty();
            Directory.Exists(StoreDir(repairHome, goneKey)).ShouldBeFalse();
            Directory.Exists(StoreDir(dropHome, goneKey)).ShouldBeFalse();
        }
        finally
        {
            Clean(repairHome, dropHome, goneRoot);
        }
    }

    [Fact]
    public async Task EmptyStoreDir_CountsAsAStoreDir_InDryRunAndInPrunedTotals()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            var goneKey = Key("prune-empty", goneRoot);
            WriteRegistry(home, Row("prune-empty", goneRoot, goneKey));
            Directory.CreateDirectory(StoreDir(home, goneKey));
            Directory.Delete(goneRoot, recursive: true);

            var dry = await RunAsync(home, "workspace", "repair", "--dry-run");
            var run = await RunAsync(home, "workspace", "repair", "--yes");

            dry.ExitCode.ShouldBe(0);
            dry.StdOut.ShouldContain("1 store dir(s) totalling 0 B would be removed");
            dry.StdOut.ShouldNotContain("registry rows only");
            run.ExitCode.ShouldBe(0);
            ResultLine(run.StdOut, goneKey).ShouldStartWith("  - pruned row + store (0 B)");
            run.StdOut.ShouldContain("Pruned 1 stale row(s) · 1 store dir(s) removed (0 B).");
            run.StdOut.ShouldNotContain("no store dirs existed");
            Directory.Exists(StoreDir(home, goneKey)).ShouldBeFalse();
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_StoreDirUndeletable_ReportsRowsItAlreadyRemoved_ExitNonZero()
    {
        using var fixture = BlockedSweepFixture.Create();

        var run = await RunAsync(fixture.Home, "workspace", "repair", "--yes");

        run.ExitCode.ShouldBe(1);
        ResultLine(run.StdOut, fixture.GoneKey).ShouldStartWith("  - pruned row + store (");
        run.StdOut.ShouldContain("x not removed: prune-zulu");
        run.StdOut.ShouldContain("Sweep stopped:");
        run.StdOut.ShouldContain("1 stale row(s) already removed, 1 store dir(s) gone");
        run.StdOut.ShouldContain("Repair report: 0 adopted, 0 need --root, 1 stale row(s) pruned, 0 husk row(s) pruned.");
        run.StdOut.ShouldNotContain("2 stale row(s) pruned");
        run.StdErr.ShouldContain("error:");
        run.StdOut.ShouldNotContain(fixture.BlockedDir);
        run.StdErr.ShouldNotContain(fixture.BlockedDir);
        ReadRegistry(fixture.Home).Select(row => row.StoreKey).ShouldBe([fixture.BlockedKey]);
        Directory.Exists(StoreDir(fixture.Home, fixture.GoneKey)).ShouldBeFalse();
        Directory.Exists(fixture.BlockedDir).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_Json_StoreDirUndeletable_WritesPartialDocumentAndErrorEnvelope()
    {
        using var fixture = BlockedSweepFixture.Create();
        var goneBytes = StoreBytes(fixture.Home, fixture.GoneKey);

        var run = await RunAsync(fixture.Home, "workspace", "repair", "--yes", "--json");

        run.ExitCode.ShouldBe(1);
        using var document = JsonDocument.Parse(run.StdOut.Trim());
        var prune = document.RootElement.GetProperty("prune");
        prune.GetProperty("mode").GetString().ShouldBe("aborted");
        prune.GetProperty("storesRemoved").GetInt32().ShouldBe(1);
        prune.GetProperty("totalStoreBytes").GetInt64().ShouldBe(goneBytes);
        var rows = prune.GetProperty("rows");
        rows.EnumerateArray().Select(row => row.GetProperty("action").GetString())
            .ShouldBe(["pruned-row-and-store", "prune-failed"]);
        PrunedRow(rows, fixture.GoneKey).GetProperty("removedPaths").EnumerateArray()
            .Select(path => path.GetString()).ShouldBe([StoreDir(fixture.Home, fixture.GoneKey)]);
        var failed = PrunedRow(rows, fixture.BlockedKey);
        failed.GetProperty("failure").GetString()!.ShouldNotContain(fixture.BlockedDir);
        failed.GetProperty("removedPaths").GetArrayLength().ShouldBe(0);
        StaleRow(document, fixture.BlockedKey).GetProperty("storeState").GetString().ShouldBe("unreadable");
        using var error = JsonDocument.Parse(run.StdErr.Trim());
        error.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("workspace_error");
        ReadRegistry(fixture.Home).Select(row => row.StoreKey).ShouldBe([fixture.BlockedKey]);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task DryRun_ListsStaleRows_TellsApartUnmeasuredMissingAndUnreadable()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var blockedRoot = Canonical(TestDirs.NewTempDir());
        var absentRoot = Canonical(TestDirs.NewTempDir());
        var evilRoot = Canonical(TestDirs.NewTempDir());
        var goneKey = Key("prune-gone", goneRoot);
        var blockedKey = Key("prune-zulu", blockedRoot);
        var absentKey = Key("prune-absent", absentRoot);
        try
        {
            WriteRegistry(home,
                Row("prune-gone", goneRoot, goneKey),
                Row("prune-zulu", blockedRoot, blockedKey),
                Row("prune-absent", absentRoot, absentKey),
                Row("prune-evil", evilRoot, ".."));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 240));
            WriteStoreDir(home, blockedKey, "prune-zulu", blockedRoot, ("meta.db", 240));
            foreach (var root in new[] { goneRoot, blockedRoot, absentRoot, evilRoot })
            {
                Directory.Delete(root, recursive: true);
            }

            File.SetUnixFileMode(StoreDir(home, blockedKey), UnixFileMode.None);
            var run = await RunAsync(home, "workspace", "repair", "--dry-run");

            run.ExitCode.ShouldBe(0);
            StaleLine(run.StdOut, goneKey).ShouldMatch(@"· \d+(\.\d)? (B|KB|MB|GB)$");
            StaleLine(run.StdOut, absentKey).ShouldEndWith("· no store dir");
            StaleLine(run.StdOut, blockedKey).ShouldEndWith("· size unknown, store dir unreadable");
            StaleLine(run.StdOut, "prune-evil (")
                .ShouldEndWith("· size not measured, store key is not a single path segment");
            run.StdOut.ShouldContain("2 store dir(s) totalling at least");
        }
        finally
        {
            File.SetUnixFileMode(
                StoreDir(home, blockedKey),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Clean(home, goneRoot, blockedRoot, absentRoot, evilRoot);
        }
    }

    private static long StoreBytes(string home, string storeKey) =>
        Directory.EnumerateFiles(StoreDir(home, storeKey), "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private static JsonElement StaleRow(JsonDocument document, string storeKey) =>
        document.RootElement.GetProperty("stale").EnumerateArray()
            .Single(row => row.GetProperty("storeKey").GetString() == storeKey);

    private static JsonElement PrunedRow(JsonElement rows, string storeKey) =>
        rows.EnumerateArray().Single(row => row.GetProperty("storeKey").GetString() == storeKey);

    [SupportedOSPlatform("linux")]
    private sealed class BlockedSweepFixture : IDisposable
    {
        private BlockedSweepFixture(string home, string goneRoot, string blockedRoot, string goneKey,
            string blockedKey, string blockedDir)
        {
            Home = home;
            GoneRoot = goneRoot;
            BlockedRoot = blockedRoot;
            GoneKey = goneKey;
            BlockedKey = blockedKey;
            BlockedDir = blockedDir;
        }

        public string Home { get; }

        public string GoneRoot { get; }

        public string BlockedRoot { get; }

        public string GoneKey { get; }

        public string BlockedKey { get; }

        public string BlockedDir { get; }

        public static BlockedSweepFixture Create()
        {
            var home = TestDirs.NewTempDir();
            var goneRoot = Canonical(TestDirs.NewTempDir());
            var blockedRoot = Canonical(TestDirs.NewTempDir());
            var goneKey = Key("prune-gone", goneRoot);
            var blockedKey = Key("prune-zulu", blockedRoot);
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey), Row("prune-zulu", blockedRoot, blockedKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot, ("meta.db", 320));
            var blockedDir = StoreDir(home, blockedKey);
            WriteStoreDir(home, blockedKey, "prune-zulu", blockedRoot, ("meta.db", 320));
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(blockedRoot, recursive: true);
            File.SetUnixFileMode(blockedDir, UnixFileMode.None);
            return new BlockedSweepFixture(home, goneRoot, blockedRoot, goneKey, blockedKey, blockedDir);
        }

        public void Dispose()
        {
            File.SetUnixFileMode(
                BlockedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Clean(Home, GoneRoot, BlockedRoot);
        }
    }

    private static string StaleLine(string stdOut, string storeKey) =>
        stdOut.Split('\n').Single(line => line.Contains(storeKey));

    private static string ResultLine(string stdOut, string storeKey) =>
        stdOut.Split('\n').Single(line => line.Contains($"({storeKey}) — performed:"));

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(RegistryPath(home)), SqueezrJson.FileOptions) ?? [];

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(RegistryPath(home), JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static void WriteStoreDir(
        string home,
        string storeKey,
        string name,
        string? rootPath,
        (string Name, int Bytes)? file)
    {
        var storeDir = StoreDir(home, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = name, RootPath = rootPath }, SqueezrJson.FileOptions));
        if (file is { } payload)
        {
            File.WriteAllText(Path.Combine(storeDir, payload.Name), new string('x', payload.Bytes));
        }
    }

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static string Key(string name, string canonicalRoot) =>
        WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static string RegistryPath(string home) => Path.Combine(home, SqueezrPaths.RegistryFileName);

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string home, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), args);

    private static void Clean(string home, params string[] extraDirs)
    {
        foreach (var dir in new[] { home }.Concat(extraDirs))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
