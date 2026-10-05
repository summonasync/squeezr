using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using System.Runtime.Versioning;

namespace Squeezr.Tests.Indexing;

using static Squeezr.Tests.Indexing.IngestionWorkspaces;


// Every route that can put a path into a store: the full pass, the paced delta the daemon runs, and the
// single-file reindex a read call triggers. The guard has to answer on all three, on the path alone, and
// report a count without ever repeating the name it is protecting.
public class SecretGuardIngestionTests
{
    [Fact]
    public void Index_FullPass_WritesNoRowForAnyProtectedPath()
    {
        var workspace = NewWorkspaceWithSecrets();

        var result = new WorkspaceIndexer().Index(workspace);

        StoredPaths(workspace).ShouldBe([AppPath], StringComparer.Ordinal);
        result.Repos.ShouldHaveSingleItem().Files.ShouldBe(1);
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        foreach (var file in store.ListFiles(RepoId(store)))
        {
            store.ListChunksByFile(file.Id)
                .ShouldNotContain(c => c.Text.Contains(EnvCanary, StringComparison.Ordinal));
        }

        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(PemPath);
    }

    [Fact]
    public void Index_PacedDelta_WithheldPathIsSkippedNotFailed()
    {
        var workspace = NewWorkspaceWithSecrets();
        using var sessions = new SemanticSessionPool();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, sessions: sessions);

        var batch = new List<string>
        {
            Full(workspace, EnvPath),
            Full(workspace, PemPath),
            Full(workspace, AppPath),
        };
        var result = indexer.Index(workspace, changedPaths: batch, sessions: sessions);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.GuardWithheld.ShouldBe(2);
        repo.GuardWithheldBuiltIns.ShouldBe(2);
        repo.Skipped.ShouldBeGreaterThanOrEqualTo(2);
        StoredPaths(workspace).ShouldBe([AppPath], StringComparer.Ordinal);
    }

    [Fact]
    public void IndexFile_InlineReindex_RefusesWithoutReadingTheFile()
    {
        var workspace = NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);

        var doc = new WorkspaceIndexer().IndexFile(workspace, "demo", EnvPath);

        doc.ShouldBeNull();
        StoredPaths(workspace).ShouldNotContain(EnvPath);
    }

    [Fact]
    public void IndexFile_NullPathShape_IsRefusedWithoutCrashing()
    {
        var workspace = NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);

        Should.NotThrow(() => new WorkspaceIndexer().IndexFile(workspace, "demo", null!));
        StoredPaths(workspace).ShouldNotContain(EnvPath);
    }

    [Fact]
    public void FreshnessGuard_ProtectedCandidate_CannotReintroduceWhatThePassDropped()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.PlantPreGuardRows(workspace, [EnvPath]);
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.ListFiles(RepoId(store)).ShouldContain(f => f.Path == EnvPath);
        }

        new WorkspaceIndexer().Index(workspace);
        var refusedByThePass = LedgerWithheld(workspace);

        var refreshed = FreshnessGuard.CheckAndRefresh(workspace, [("demo", EnvPath), ("demo", AppPath)]);

        StoredPaths(workspace).ShouldBe([AppPath], StringComparer.Ordinal);
        refreshed.Refreshed.ShouldBeEmpty();
        refreshed.Stale.ShouldBeEmpty();
        LedgerWithheld(workspace).ShouldBe(refusedByThePass);
    }

    [Fact]
    public void Index_FullPass_DropsAPreGuardRowAndReportsTheCount()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.PlantPreGuardRows(workspace, [EnvPath, PemPath]);

        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Deleted.ShouldBe(2);
        repo.GuardDropped.ShouldBe(2);
        repo.GuardWithheld.ShouldBe(2);
        StoredPaths(workspace).ShouldBe([AppPath], StringComparer.Ordinal);
    }

    [Fact]
    public void Index_GuardRefusalIsReportedAsAWarningThatNamesTheOrigin()
    {
        var workspace = NewWorkspaceWithSecrets();

        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldContain(
            "secret guard: 2 file(s) withheld from indexing (built-in patterns)");
        string.Join("|", result.Warnings).ShouldNotContain("monkey.pem");
    }

    [Fact]
    public void Index_ConfiguredMaskReportsItsOwnOrigin()
    {
        var workspace = NewWorkspaceWithSecrets();
        File.Delete(Full(workspace, EnvPath));
        File.Delete(Full(workspace, PemPath));
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        WriteFile(workspace, "notes/tokens.txt", "T2-CANARY-TOKENS-VALUE\n");

        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldContain(
            "secret guard: 1 file(s) withheld from indexing (indexing.secretGuardExtra)");
        result.Repos.ShouldHaveSingleItem().GuardWithheldBuiltIns.ShouldBe(0);
    }

    [Fact]
    public void Index_BothOriginsAreCountedApart()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        WriteFile(workspace, "notes/tokens.txt", "T2-CANARY-TOKENS-VALUE\n");

        new WorkspaceIndexer().Index(workspace);
        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.GuardWithheld.ShouldBe(3);
        repo.GuardWithheldBuiltIns.ShouldBe(2);
        result.Warnings.ShouldContain(
            "secret guard: 3 file(s) withheld from indexing (2 built-in patterns, 1 indexing.secretGuardExtra)");
    }

    [Fact]
    public void GuardHook_HoldsWithGitignoreRespectedAndAnEmptyIgnoreList()
    {
        var workspace = NewWorkspaceWithSecrets();
        File.WriteAllText(Path.Combine(RepoRoot(workspace), ".squeezrignore"), $"!{EnvPath}\n");
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Indexing = config.Indexing with { Ignore = [], RespectGitignore = false }
        });

        new WorkspaceIndexer().Index(workspace);

        StoredPaths(workspace).ShouldNotContain(EnvPath);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Walk_GuardDecidesBeforeTheUnreadableBranch_NamingNothingInAWarning()
    {
        var workspace = NewWorkspaceWithSecrets();
        var rules = IgnoreRuleset.For(RepoRoot(workspace), SqueezrConfig.CreateDefault());
        File.SetUnixFileMode(Full(workspace, PemPath), UnixFileMode.None);
        var warnings = new List<string>();

        var guard = Guard(workspace);
        var walk = RepoFileWalker.Walk(RepoRoot(workspace), rules, guard, 512, warnings.Add);

        guard.Withheld.ShouldBe(2);
        walk.Skipped.ShouldBe(2);
        string.Join("|", warnings).ShouldNotContain("monkey.pem");
    }

    [Fact]
    public void Ingestion_RuleOutsAnEmptyPathWithoutRefusingIt()
    {
        var workspace = NewWorkspaceWithSecrets();
        var guard = Guard(workspace);

        guard.Deny(string.Empty).ShouldBeFalse();
        guard.Deny("notes/plain.txt").ShouldBeFalse();
        guard.Withheld.ShouldBe(0);
        Should.Throw<ArgumentNullException>(() => SecretGuard.IsProtected(null!, []));
    }

    [Fact]
    public void Ingestion_ACorruptRowWithoutAPath_LeavesTheStoreWithoutCrashingThePass()
    {
        var workspace = NewWorkspaceWithSecrets();
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.UpsertFile(new FileDocument
            {
                Id = store.GetNextFileId(),
                RepoId = store.ListRepos().First().Id,
                Path = null!,
                Sha256 = "deadbeef",
            });
        }

        var result = new WorkspaceIndexer().Index(workspace);

        result.Repos.ShouldHaveSingleItem().Deleted.ShouldBe(1);
        using var check = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        check.ListAllFiles().ShouldNotContain(f => string.IsNullOrEmpty(f.Path));
    }

    [Fact]
    public void Ingestion_DecidesTheSameAsTheClassifierForEveryShape()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt", "**/*.crt");
        var guard = Guard(workspace);
        var extras = ConfigFile.Load(workspace).Indexing.SecretGuardExtra;
        var shapes = new[]
        {
            EnvPath, "web/.env.production", "web/.env.example", PemPath, "config/secrets.json",
            "notes/tokens.txt", "deploy/server.crt", AppPath, "src/App.csproj", string.Empty,
        };

        foreach (var shape in shapes)
        {
            guard.IsProtected(shape).ShouldBe(
                SecretGuard.IsProtected(shape, extras), $"ingestion diverged on '{shape}'");
        }
    }

    [Fact]
    public void Ingestion_SourcesNeverReimplementThePatternList()
    {
        var solutionRoot = FindSolutionRoot();
        var offenders = new List<string>();
        var files = new[]
        {
            "src/Squeezr.Core/Indexing/RepoFileWalker.cs",
            "src/Squeezr.Core/Indexing/WorkspaceIndexer.cs",
            "src/Squeezr.Core/Indexing/Roslyn/SemanticIndexPhase.cs",
            "src/Squeezr.Core/Indexing/Watch/WatchEventFilter.cs",
            "src/Squeezr.Core/Indexing/Watch/WorkspaceWatcher.cs",
            "src/Squeezr.Core/Indexing/FreshnessGuard.cs",
            "src/Squeezr.Core/Indexing/StaleFileDetector.cs",
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(solutionRoot, file));
            if (NamesAMask(source))
            {
                offenders.Add($"{file} carries a mask literal");
            }

            if (source.Contains("SecretGuard.IsProtected(", StringComparison.Ordinal))
            {
                offenders.Add($"{file} calls the classifier past the reader");
            }
        }

        offenders.ShouldBeEmpty();
        NamesAMask(File.ReadAllText(
            Path.Combine(solutionRoot, "src/Squeezr.Core/Indexing/SecretGuard.cs"))).ShouldBeTrue();
    }

    [Fact]
    public void ProgressStream_NeverCarriesAWithheldPath_InAnyPhase()
    {
        var workspace = NewWorkspaceWithSecrets();
        var progress = new RecordingProgress();

        var result = new WorkspaceIndexer().Index(workspace, progress: progress);

        progress.ItemsOf(IndexPhase.Files).ShouldBe([AppPath]);
        progress.AllItems.ShouldNotContain(EnvPath);
        progress.AllItems.ShouldNotContain(PemPath);
        result.Repos.ShouldHaveSingleItem().GuardWithheld.ShouldBe(2);
    }

    private sealed class RecordingProgress : IIndexProgress
    {
        private readonly List<(IndexPhase Phase, string Item)> _items = [];
        private IndexPhase _phase;

        public IReadOnlyList<string> AllItems => [.. _items.Select(i => i.Item)];

        public IReadOnlyList<string> ItemsOf(IndexPhase phase) =>
            [.. _items.Where(i => i.Phase == phase).Select(i => i.Item)];

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems) => _phase = phase;

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null) =>
            _items.Add((_phase, currentItem));

        public void RepoCompleted(string repoName)
        {
        }
    }

    private static bool NamesAMask(string source) =>
        new[] { ".env", ".pem", "id_rsa", ".npmrc", "secrets.json", ".kdbx" }
            .Any(mask => source.Contains(mask, StringComparison.Ordinal));

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Squeezr.slnx not found above the test assembly");
    }

    [Fact]
    public void WatchEventFilter_KeepsDecidingOnLanguage_NotOnTheGuard()
    {
        var workspace = NewWorkspaceWithSecrets();
        var repoRoot = RepoRoot(workspace);
        var filter = new WatchEventFilter(
            workspace, [repoRoot], new Dictionary<string, IgnoreRuleset> { [repoRoot] = IgnoreRuleset.For(repoRoot, ConfigFile.Load(workspace)) });

        filter.ShouldTrack(Full(workspace, AppPath), pathMayBeGone: false).ShouldBeTrue();
        filter.ShouldTrack(Full(workspace, "notes/plain.zzz"), pathMayBeGone: false).ShouldBeFalse();
        filter.ShouldTrack(Full(workspace, "config/secrets.json"), pathMayBeGone: false)
            .ShouldBe(filter.ShouldTrack(Full(workspace, "config/settings.json"), pathMayBeGone: false));

        var pass = new WorkspaceIndexer().Index(workspace);
        pass.Repos.ShouldHaveSingleItem().GuardWithheld.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Ledger_CarriesTheIngestionTallyOfTheLastPass()
    {
        var workspace = NewWorkspaceWithSecrets();

        new WorkspaceIndexer().Index(workspace);
        var status = new WorkspaceManager(workspace).GetStatus();

        var repo = status.Repos.ShouldHaveSingleItem();
        repo.GuardWithheld.ShouldBe(2);
        repo.GuardWithheldBuiltIns.ShouldBe(2);
        repo.GuardDropped.ShouldBe(0);
    }

    [Fact]
    public void Ledger_WithoutARefusalStaysQuiet()
    {
        var workspace = NewWorkspaceWithSecrets();
        File.Delete(Full(workspace, EnvPath));
        File.Delete(Full(workspace, PemPath));

        new WorkspaceIndexer().Index(workspace);
        var status = new WorkspaceManager(workspace).GetStatus();

        status.Repos.ShouldAllBe(r => r.GuardWithheld == 0 && r.GuardDropped == 0);
    }

    [Fact]
    public void DropWarnings_NeverQuoteAProtectedPath()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.PlantPreGuardRows(workspace, [EnvPath, PemPath]);
        File.Delete(Full(workspace, EnvPath));
        File.Delete(Full(workspace, PemPath));
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var manifest = WorkspaceManifestFile.Load(workspace);
        var warnings = new List<string>();

        WorkspaceIndexer.RebuildTrigramIndex(
            workspace, store, manifest, warnings, new Dictionary<uint, string>(),
            SqueezrPaths.TrigramIndexPath(workspace));

        string.Join("|", warnings).ShouldNotContain("monkey.pem");
        string.Join("|", warnings).ShouldNotContain("dropping '.env'");
        warnings.ShouldContain(w => w.Contains("2 protected path(s)", StringComparison.Ordinal));
        warnings.ShouldContain(w => w.Contains("file is missing on disk", StringComparison.Ordinal));
    }

    [Fact]
    public void DropWarnings_StillNameAnOrdinaryMissingFile()
    {
        var workspace = NewWorkspaceWithSecrets();
        WriteFile(workspace, "src/Gone.cs", "class Gone { }\n");
        SecretGuardFixture.PlantPreGuardRows(workspace, ["src/Gone.cs"]);
        File.Delete(Full(workspace, "src/Gone.cs"));
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var warnings = new List<string>();

        WorkspaceIndexer.RebuildTrigramIndex(
            workspace, store, WorkspaceManifestFile.Load(workspace), warnings,
            new Dictionary<uint, string>(), SqueezrPaths.TrigramIndexPath(workspace));

        warnings.ShouldContain("dropping 'src/Gone.cs' from index store: file is missing on disk");
    }

    [Fact]
    public void StaleReport_ListsNoProtectedPathButStillTheOrdinaryOne()
    {
        var workspace = NewWorkspaceWithSecrets();
        SecretGuardFixture.PlantPreGuardRows(workspace, [EnvPath]);
        WriteFile(workspace, "src/Extra.cs", "class Extra { }\n");

        var stale = StaleFileDetector.Detect(workspace);

        stale.ShouldContain("demo/src/Extra.cs");
        stale.ShouldNotContain("demo/.env");
    }

    private static SecretGuardIngestion Guard(string workspace) => SecretGuardIngestion.Load(workspace);
}
