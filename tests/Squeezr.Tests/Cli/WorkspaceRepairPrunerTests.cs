using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WorkspaceRepairPrunerTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LiveName = "prune-live";
    private const string WithStoreName = "prune-with-store";
    private const string NoStoreName = "prune-no-store";
    private const string HuskName = "prune-husk";
    private const string EvilName = "prune-evil";
    private const string NonLatinName = "проект-проект";
    private const string BlockedName = "prune-zulu";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _liveRoot;
    private readonly string _withStoreRoot;
    private readonly string _noStoreRoot;
    private readonly string _evilRoot;
    private readonly string _liveKey;
    private readonly string _withStoreKey;
    private readonly string _noStoreKey;
    private readonly string _huskKey;
    private readonly string _withStoreDir;
    private readonly long _withStoreBytes;

    public WorkspaceRepairPrunerTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();

        _liveRoot = Canonical(NewTempDir());
        _withStoreRoot = Canonical(NewTempDir());
        _noStoreRoot = Canonical(NewTempDir());
        _evilRoot = Canonical(NewTempDir());
        _liveKey = StoreKey(LiveName, _liveRoot);
        _withStoreKey = StoreKey(WithStoreName, _withStoreRoot);
        _noStoreKey = StoreKey(NoStoreName, _noStoreRoot);
        _huskKey = StoreKey(HuskName, Canonical(_home));

        WriteStoreDir(_liveKey, LiveName, _liveRoot, ("meta.db", 7));
        _withStoreDir = WriteStoreDir(
            _withStoreKey, WithStoreName, _withStoreRoot, ("meta.db", 1024), ("nested/state.json", 2560));
        _withStoreBytes = TotalBytes(_withStoreDir);
        File.WriteAllText(Path.Combine(_home, "canary.txt"), "keep me");

        WriteRegistry(
            Row(LiveName, _liveRoot, _liveKey),
            Row(WithStoreName, _withStoreRoot, _withStoreKey),
            Row(NoStoreName, _noStoreRoot, _noStoreKey),
            Row(HuskName, Canonical(_home), _huskKey),
            Row(EvilName, _evilRoot, ".."));

        Directory.Delete(_withStoreRoot, recursive: true);
        Directory.Delete(_noStoreRoot, recursive: true);
        Directory.Delete(_evilRoot, recursive: true);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void Gate_NoCandidates_ReturnsNone(bool dryRun, bool yes, bool promptAvailable) =>
        WorkspaceRepairPruner.Gate(0, dryRun, yes, promptAvailable).ShouldBe(RepairPruneGate.None);

    [Fact]
    public void Gate_NoFlagsAndNoPromptChannel_Refuses() =>
        WorkspaceRepairPruner.Gate(2, dryRun: false, yes: false, promptAvailable: false)
            .ShouldBe(RepairPruneGate.Refuse);

    [Fact]
    public void Gate_PromptChannelWithYes_Pruns() =>
        WorkspaceRepairPruner.Gate(2, dryRun: false, yes: true, promptAvailable: true)
            .ShouldBe(RepairPruneGate.Confirmed);

    [Fact]
    public void Gate_PromptChannelWithoutFlags_AskTheHuman() =>
        WorkspaceRepairPruner.Gate(2, dryRun: false, yes: false, promptAvailable: true)
            .ShouldBe(RepairPruneGate.Prompt);

    [Fact]
    public void Gate_DryRunListsInsteadOfDeleting() =>
        WorkspaceRepairPruner.Gate(2, dryRun: true, yes: false, promptAvailable: true)
            .ShouldBe(RepairPruneGate.DryRun);

    [Fact]
    public async Task RunAsync_Refused_TouchesNothing()
    {
        var before = ReadRegistry().Select(row => row.StoreKey).ToList();

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Refuse, Candidates(), new TestConsole(), CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModeRefused);
        summary.Rows.ShouldBeEmpty();
        ReadRegistry().Select(row => row.StoreKey).ShouldBe(before);
        Directory.Exists(_withStoreDir).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_Confirmed_RemovesStaleRows_KeepsLiveRowAndHusk()
    {
        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Confirmed, Candidates(), new TestConsole(), CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModePruned);
        var keys = ReadRegistry().Select(row => row.StoreKey).ToList();
        keys.ShouldContain(_liveKey);
        keys.ShouldContain(_huskKey);
        keys.ShouldNotContain(_withStoreKey);
        keys.ShouldNotContain(_noStoreKey);
        Directory.Exists(_withStoreDir).ShouldBeFalse();
        Directory.Exists(StoreDir(_liveKey)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");

        var rowAndStore = summary.Rows.Single(row => row.StoreKey == _withStoreKey);
        rowAndStore.Action.ShouldBe(WorkspaceRepairPruner.ActionRowAndStore);
        rowAndStore.Category.ShouldBe(WorkspaceRepairPruner.CategoryStale);
        rowAndStore.StoreBytes.ShouldBe(_withStoreBytes);
        var rowOnly = summary.Rows.Single(row => row.StoreKey == _noStoreKey);
        rowOnly.Action.ShouldBe(WorkspaceRepairPruner.ActionRowOnly);
        rowOnly.RemovedPaths.ShouldBeEmpty();
        summary.TotalStoreBytes.ShouldBe(_withStoreBytes);
    }

    [Fact]
    public async Task RunAsync_Confirmed_NamesOnlyPathsItDeleted()
    {
        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Confirmed, Candidates(), new TestConsole(), CancellationToken.None);

        var deleted = summary.Rows.SelectMany(row => row.RemovedPaths).ToList();
        deleted.ShouldHaveSingleItem().ShouldBe(_withStoreDir);
        Directory.Exists(_withStoreDir).ShouldBeFalse();
        summary.Rows.Where(row => row.Action != WorkspaceRepairPruner.ActionRowAndStore)
            .SelectMany(row => row.RemovedPaths).ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_PromptAccepted_AsksThenPrunes()
    {
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("y");

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Prompt, Candidates(), console, CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModePruned);
        console.Output.ShouldContain("Prune 3 stale row(s) and their store dirs?");
        ReadRegistry().Select(row => row.StoreKey).ShouldNotContain(_withStoreKey);
    }

    [Fact]
    public async Task RunAsync_PromptDeclined_KeepsEveryRowAndStore()
    {
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("n");
        var before = ReadRegistry().Select(row => row.StoreKey).ToList();

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Prompt, Candidates(), console, CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModeDeclined);
        summary.Rows.ShouldBeEmpty();
        ReadRegistry().Select(row => row.StoreKey).ShouldBe(before);
        Directory.Exists(_withStoreDir).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_TraversalStoreKey_LeftAlone_HomeDirSurvives()
    {
        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Confirmed, Candidates(), new TestConsole(), CancellationToken.None);

        var evil = summary.Rows.Single(row => row.StoreKey == "..");
        evil.Action.ShouldBe(WorkspaceRepairPruner.ActionSkippedUnsafeKey);
        evil.RemovedPaths.ShouldBeEmpty();
        Directory.Exists(_home).ShouldBeTrue();
        File.Exists(Path.Combine(_home, "canary.txt")).ShouldBeTrue();
        ReadRegistry().ShouldContain(row => row.StoreKey == "..");
    }

    [Fact]
    public async Task RunAsync_Confirmed_HuskCandidate_PrunesRowAndStore_HomeDirectorySurvives()
    {
        var huskDir = WriteStoreDir(_huskKey, HuskName, Canonical(_home), ("meta.db", 256));
        var scan = OrphanScanner.Scan();
        var candidates = WorkspaceRepairPruner.HuskCandidates(
            scan.Husks, scan.Stale.Select(row => row.StoreKey).ToHashSet(StringComparer.Ordinal));

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Confirmed, candidates, new TestConsole(), CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModePruned);
        var row = summary.Rows.ShouldHaveSingleItem();
        row.Category.ShouldBe(WorkspaceRepairPruner.CategoryHusk);
        row.Action.ShouldBe(WorkspaceRepairPruner.ActionRowAndStore);
        row.RemovedPaths.ShouldHaveSingleItem().ShouldBe(huskDir);
        summary.Rows.SelectMany(entry => entry.RemovedPaths).ShouldHaveSingleItem().ShouldBe(huskDir);
        ReadRegistry().Select(entry => entry.StoreKey).ShouldNotContain(_huskKey);
        ReadRegistry().Select(entry => entry.StoreKey).ShouldContain(_liveKey);
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    [Fact]
    public async Task RunAsync_Prompt_MixedCandidates_QuestionNamesBothCategories_AndDeclineKeepsEverything()
    {
        var huskDir = WriteStoreDir(_huskKey, HuskName, Canonical(_home), ("meta.db", 64));
        var scan = OrphanScanner.Scan();
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates = WorkspaceRepairPruner
            .Candidates(scan.Stale, new HashSet<string>(StringComparer.Ordinal))
            .Concat(WorkspaceRepairPruner.HuskCandidates(
                scan.Husks, scan.Stale.Select(row => row.StoreKey).ToHashSet(StringComparer.Ordinal)))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.StoreKey, StringComparer.Ordinal)
            .ToList();
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("n");
        var before = ReadRegistry().Select(row => row.StoreKey).ToList();

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Prompt, candidates, console, CancellationToken.None);

        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModeDeclined);
        summary.Rows.ShouldBeEmpty();
        console.Output.ShouldContain("Prune 4 row(s) (3 stale, 1 husk) and their store dirs?");
        ReadRegistry().Select(row => row.StoreKey).ShouldBe(before);
        Directory.Exists(_withStoreDir).ShouldBeTrue();
        Directory.Exists(huskDir).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_NonLatinStoreKey_PrunsRowAndStore()
    {
        var root = Canonical(NewTempDir());
        var storeKey = StoreKey(NonLatinName, root);
        var storeDir = WriteStoreDir(storeKey, NonLatinName, root, ("meta.db", 64));
        WriteRegistry(Row(LiveName, _liveRoot, _liveKey), Row(NonLatinName, root, storeKey));
        Directory.Delete(root, recursive: true);

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.Confirmed, Candidates(), new TestConsole(), CancellationToken.None);

        storeKey.ShouldStartWith("проект-проект-");
        summary.Mode.ShouldBe(WorkspaceRepairPruner.ModePruned);
        var row = summary.Rows.ShouldHaveSingleItem();
        row.StoreKey.ShouldBe(storeKey);
        row.Action.ShouldBe(WorkspaceRepairPruner.ActionRowAndStore);
        row.RemovedPaths.ShouldHaveSingleItem().ShouldBe(storeDir);
        ReadRegistry().ShouldHaveSingleItem().StoreKey.ShouldBe(_liveKey);
        File.Exists(Path.Combine(_home, "canary.txt")).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task RunAsync_StoreDirUndeletable_AbortsSweepAndKeepsRecordOfWhatWasRemoved()
    {
        var extra = AddStaleRowWithStore(BlockedName);
        File.SetUnixFileMode(extra.Dir, UnixFileMode.None);
        try
        {
            var summary = await WorkspaceRepairPruner.RunAsync(
                RepairPruneGate.Confirmed, Candidates(), new TestConsole(), CancellationToken.None);

            summary.Mode.ShouldBe("aborted");
            summary.Rows.Select(row => row.Action).ShouldBe([
                WorkspaceRepairPruner.ActionSkippedUnsafeKey,
                WorkspaceRepairPruner.ActionRowOnly,
                WorkspaceRepairPruner.ActionRowAndStore,
                WorkspaceRepairPruner.ActionPruneFailed,
            ]);
            summary.TotalStoreBytes.ShouldBe(_withStoreBytes);

            var failed = summary.Rows.Single(row => row.StoreKey == extra.Key);
            var failure = failed.Failure.ShouldNotBeNull();
            failure.ShouldNotContain(extra.Dir);
            failure.ShouldNotContain(_home);
            failed.RemovedPaths.ShouldBeEmpty();

            Directory.Exists(_withStoreDir).ShouldBeFalse();
            Directory.Exists(extra.Dir).ShouldBeTrue();
            var keys = ReadRegistry().Select(row => row.StoreKey).ToList();
            keys.ShouldNotContain(_withStoreKey);
            keys.ShouldNotContain(_noStoreKey);
            keys.ShouldContain(_liveKey);
            keys.ShouldContain(extra.Key);
            keys.ShouldContain("..");
            File.Exists(Path.Combine(_home, "canary.txt")).ShouldBeTrue();
        }
        finally
        {
            File.SetUnixFileMode(
                extra.Dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task RunAsync_DryRun_PlansEveryCandidateAndChangesNothing()
    {
        var before = ReadRegistry().Select(row => row.StoreKey).ToList();
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates = [.. Candidates().Where(row => row.StoreKey != "..")];

        var summary = await WorkspaceRepairPruner.RunAsync(
            RepairPruneGate.DryRun, candidates, new TestConsole(), CancellationToken.None);

        summary.Mode.ShouldBe("dry-run");
        summary.Rows.Select(row => row.StoreKey).ShouldBe([_noStoreKey, _withStoreKey]);
        summary.Rows.Select(row => row.Action).Distinct().ShouldHaveSingleItem().ShouldBe("would-prune");
        summary.Rows.ShouldAllBe(row => row.Action == WorkspaceRepairPruner.ActionWouldPrune);
        summary.Rows.ShouldAllBe(row => row.RemovedPaths.Count == 0);
        summary.TotalStoreBytes.ShouldBe(_withStoreBytes);
        ReadRegistry().Select(row => row.StoreKey).ShouldBe(before);
        Directory.Exists(_withStoreDir).ShouldBeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("a/../b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("")]
    public void Candidates_StoreKeyThatLeavesWorkspaces_IsNotMeasured(string storeKey)
    {
        var row = Candidate(storeKey);

        row.StoreState.ShouldBe(WorkspaceRepairPruner.StoreNotMeasured);
        row.StoreBytes.ShouldBeNull();
    }

    [Theory]
    [InlineData("проект-проект-45669088")]
    [InlineData("דגם-1a2b3c4d")]
    [InlineData("café-work-1a2b3c4d")]
    public void Candidates_StoreKeyWithNonAsciiLetters_IsMeasured(string storeKey)
    {
        var storeDir = WriteStoreDir(storeKey, "measured", _withStoreRoot, ("meta.db", 128));
        try
        {
            var row = Candidate(storeKey);

            row.StoreState.ShouldBe(WorkspaceRepairPruner.StoreMeasured);
            (row.StoreBytes > 0).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(storeDir, recursive: true);
        }
    }

    [Fact]
    public void Candidates_ExcludesRowsThatAreAlsoHusks()
    {
        var stale = new StaleInfo[] { new(WithStoreName, _withStoreKey, _withStoreRoot) };

        var candidates = WorkspaceRepairPruner.Candidates(
            stale, new HashSet<string>(StringComparer.Ordinal) { _withStoreKey });

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public void HuskCandidates_MeasuresStores_TagsHusk_AndExcludesStaleKeys()
    {
        var huskDir = WriteStoreDir(_huskKey, HuskName, Canonical(_home), ("meta.db", 320));
        var scan = OrphanScanner.Scan();
        var staleKeys = scan.Stale.Select(row => row.StoreKey).ToHashSet(StringComparer.Ordinal);

        var husks = WorkspaceRepairPruner.HuskCandidates(scan.Husks, staleKeys);

        var husk = husks.ShouldHaveSingleItem();
        husk.Category.ShouldBe(WorkspaceRepairPruner.CategoryHusk);
        husk.Name.ShouldBe(HuskName);
        husk.StoreKey.ShouldBe(_huskKey);
        husk.RootPath.ShouldBe(Canonical(_home));
        husk.StoreState.ShouldBe(WorkspaceRepairPruner.StoreMeasured);
        husk.StoreBytes.ShouldBe(TotalBytes(huskDir));

        WorkspaceRepairPruner
            .HuskCandidates(scan.Husks, new HashSet<string>(StringComparer.Ordinal) { _huskKey })
            .ShouldBeEmpty();
    }

    [Fact]
    public void CandidateLabel_And_PrunedLabel_NameCategoriesForEveryShape()
    {
        var mixed = new[]
        {
            new WorkspaceRepairStaleEntry("a", "key-a", "/r", null, WorkspaceRepairPruner.StoreMissing),
            new WorkspaceRepairStaleEntry("b", "key-b", "/r", null, WorkspaceRepairPruner.StoreMissing),
            new WorkspaceRepairStaleEntry(
                "c", "key-c", "/r", null, WorkspaceRepairPruner.StoreMissing, WorkspaceRepairPruner.CategoryHusk),
        };

        WorkspaceRepairPruner.CandidateLabel(mixed).ShouldBe("3 row(s) (2 stale, 1 husk)");
        WorkspaceRepairPruner.CandidateLabel(mixed.Take(2).ToList()).ShouldBe("2 stale row(s)");
        WorkspaceRepairPruner.CandidateLabel(mixed.Skip(2).ToList()).ShouldBe("1 husk row(s)");

        var pruned = new[]
        {
            new WorkspaceRepairPrunedEntry(
                "key-a", "a", "/r", WorkspaceRepairPruner.CategoryStale,
                WorkspaceRepairPruner.ActionRowAndStore, null, []),
            new WorkspaceRepairPrunedEntry(
                "key-b", "b", "/r", WorkspaceRepairPruner.CategoryStale,
                WorkspaceRepairPruner.ActionSkippedUnsafeKey, null, []),
            new WorkspaceRepairPrunedEntry(
                "key-c", "c", "/r", WorkspaceRepairPruner.CategoryHusk,
                WorkspaceRepairPruner.ActionRowOnly, null, []),
        };

        WorkspaceRepairPruner.PrunedLabel(pruned).ShouldBe("2 row(s) (1 stale, 1 husk)");
    }

    [Fact]
    public void Candidates_OrdersByNames_MeasuresStoresRecursively()
    {
        var candidates = Candidates();

        candidates.Select(row => row.StoreKey).ShouldBe(["..", _noStoreKey, _withStoreKey]);
        candidates.Single(row => row.StoreKey == _withStoreKey).StoreBytes.ShouldBe(_withStoreBytes);
        candidates.Single(row => row.StoreKey == _withStoreKey).RootPath.ShouldBe(_withStoreRoot);
        TotalBytes(_withStoreDir, SearchOption.TopDirectoryOnly)
            .ShouldBeLessThan(_withStoreBytes);
        candidates.Single(row => row.StoreKey == _noStoreKey).StoreBytes.ShouldBeNull();
        candidates.Single(row => row.StoreKey == "..").StoreBytes.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(482, "482 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(12_900, "12.6 KB")]
    [InlineData(12_900_000, "12.3 MB")]
    [InlineData(1_500_000_000, "1.4 GB")]
    public void StoreSizeText_FormatsUnits(long bytes, string expected) =>
        StoreSizeText.Format(bytes).ShouldBe(expected);

    private IReadOnlyList<WorkspaceRepairStaleEntry> Candidates()
    {
        var scan = OrphanScanner.Scan();
        return WorkspaceRepairPruner.Candidates(
            scan.Stale,
            scan.Husks.Select(husk => husk.StoreKey).ToHashSet(StringComparer.Ordinal));
    }

    private WorkspaceRepairStaleEntry Candidate(string storeKey) =>
        WorkspaceRepairPruner.Candidates(
            [new StaleInfo("measured", storeKey, _withStoreRoot)],
            new HashSet<string>(StringComparer.Ordinal)).ShouldHaveSingleItem();

    private (string Root, string Key, string Dir) AddStaleRowWithStore(string name)
    {
        var root = Canonical(NewTempDir());
        var storeKey = StoreKey(name, root);
        var storeDir = WriteStoreDir(storeKey, name, root, ("meta.db", 64));
        WriteRegistry(
            Row(LiveName, _liveRoot, _liveKey),
            Row(WithStoreName, _withStoreRoot, _withStoreKey),
            Row(NoStoreName, _noStoreRoot, _noStoreKey),
            Row(HuskName, Canonical(_home), _huskKey),
            Row(EvilName, _evilRoot, ".."),
            Row(name, root, storeKey));
        Directory.Delete(root, recursive: true);
        return (root, storeKey, storeDir);
    }

    private List<WorkspaceRegistryEntry> ReadRegistry() =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(_home, SqueezrPaths.RegistryFileName)),
            SqueezrJson.FileOptions) ?? [];

    private string StoreDir(string storeKey) =>
        Path.Combine(_home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static long TotalBytes(string directory, SearchOption option = SearchOption.AllDirectories) =>
        Directory.EnumerateFiles(directory, "*", option).Sum(file => new FileInfo(file).Length);

    private string WriteStoreDir(string storeKey, string name, string root, params (string Relative, int Bytes)[] files)
    {
        var storeDir = StoreDir(storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = name, RootPath = root }, SqueezrJson.FileOptions));
        foreach (var (relative, bytes) in files)
        {
            var target = Path.Combine(storeDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, new string('x', bytes));
        }

        return storeDir;
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(_home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static string StoreKey(string name, string canonicalRoot) =>
        WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squeezr-prune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TestConsole InteractiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        return console;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
