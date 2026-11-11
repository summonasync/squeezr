using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Squeezr.Cli.Mcp;
using Squeezr.Cli.Mcp.Tools;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Shouldly;

namespace Squeezr.Tests.Indexing;

// The map answers with a structural verdict: one pass over the indexed rows of the scope it renders,
// answered from stat alone. These tests hold both halves — what the pass may claim about a row, and what
// the map may no longer do while claiming it (re-render, reindex).
public class StructureFreshnessCheckTests
{
    [Fact]
    public void UntouchedRows_AreAllCheckedAndNameNothing()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Class("Alpha")),
            ("src/Beta.cs", Class("Beta")),
            ("src/Gamma.cs", Class("Gamma")));

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Checked.ShouldBe(3);
        result.Withheld.ShouldBe(0);
        result.Skipped.ShouldBe(0);
        result.Missing.ShouldBeEmpty();
        result.Changed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
    }

    [Fact]
    public void RowWithoutAFileOnDisk_IsNamedMissing_AndStillCountsAsChecked()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Class("Alpha")),
            ("src/Gone.cs", Class("Gone")),
            ("src/Kept.cs", Class("Kept")));
        File.Delete(IngestionWorkspaces.Full(workspace, "src/Gone.cs"));

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Missing.ShouldBe(["demo/src/Gone.cs"]);
        result.Changed.ShouldBeEmpty();
        result.Stale.ShouldBe(["demo/src/Gone.cs"]);
        result.Checked.ShouldBe(3,
            "a row the pass walked and found bare is coverage, unlike a row it never looked at");
    }

    [Fact]
    public void RowBiggerThanTheFile_IsChangedOnSize_WithAStampThatStillReadsOld()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")));
        var path = IngestionWorkspaces.Full(workspace, "src/Alpha.cs");
        File.AppendAllText(path, "// a line that was not there when the row was written\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Changed.ShouldBe(["demo/src/Alpha.cs"]);
        result.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void FileRewrittenToTheSameSize_IsChangedOnTheStamp_BecauseThePassNeverHashes()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", "public class Alpha { int Value = 1; }\n"));
        var path = IngestionWorkspaces.Full(workspace, "src/Alpha.cs");
        var rowSize = StoredRow(workspace, "src/Alpha.cs").SizeBytes;
        File.WriteAllText(path, "public class Alpha { int Value = 2; }\n");
        File.SetLastWriteTimeUtc(path, IndexedAtUtc(workspace, "src/Alpha.cs").AddSeconds(5));

        new FileInfo(path).Length.ShouldBe(rowSize,
            "the size arm has to be inert here, so the verdict below can only come from the stamp");

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Changed.ShouldBe(["demo/src/Alpha.cs"]);
        result.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void PrefixScopesThePass_ToTheRowsTheMapRenders()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Class("Alpha")),
            ("docs/Notes.cs", Class("Notes")),
            ("docs/Tips.cs", Class("Tips")));

        var scoped = StructureFreshnessCheck.Check(workspace, ["demo"], "docs/");
        scoped.Checked.ShouldBe(2);
        scoped.Missing.ShouldBeEmpty();

        File.Delete(IngestionWorkspaces.Full(workspace, "src/Alpha.cs"));
        StructureFreshnessCheck.Check(workspace, ["demo"], "docs/").Checked.ShouldBe(2);

        var whole = StructureFreshnessCheck.Check(workspace, ["demo"]);
        whole.Checked.ShouldBe(3);
        whole.Missing.ShouldBe(["demo/src/Alpha.cs"]);
    }

    [Fact]
    public void ProtectedRow_IsCountedAsWithheld_AndNeverNamed()
    {
        var workspace = IndexedWorkspace(
            ("src/App.cs", Class("App")),
            ("deploy/keys/monkey.pem", "-----BEGIN PRIVATE KEY-----\nLEAKME\n"));
        SecretGuardFixture.PlantPreGuardRows(workspace, ["deploy/keys/monkey.pem"]);
        var ledger = IngestionWorkspaces.LedgerWithheld(workspace);

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Checked.ShouldBe(1);
        result.Withheld.ShouldBe(1);
        result.Stale.ShouldBeEmpty();
        result.Missing.ShouldNotContain(p => p.Contains("monkey", StringComparison.OrdinalIgnoreCase));
        result.Changed.ShouldNotContain(p => p.Contains("monkey", StringComparison.OrdinalIgnoreCase));
        JsonSerializer.SerializeToElement(result).GetRawText().ShouldNotContain("LEAKME");
        IngestionWorkspaces.LedgerWithheld(workspace).ShouldBe(ledger,
            "a pass that reads nothing cannot move the ingestion tally either");
    }

    [Fact]
    public void PrefixWhoseEveryRowIsProtected_ReportsWithheldCoverage_NotAbsence()
    {
        var workspace = IndexedWorkspace(
            ("secrets/TokenVault.cs", Class("TokenVault")),
            ("secrets/KeyRing.cs", Class("KeyRing")),
            ("src/App.cs", Class("App")));
        SecretGuardFixture.SetGuardExtra(workspace, "**/secrets/*.cs");

        var result = StructureFreshnessCheck.Check(workspace, ["demo"], "secrets/");

        result.Checked.ShouldBe(0);
        result.Withheld.ShouldBe(2);
        result.Missing.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
    }

    [Fact]
    public void TwoReposInTheScope_AreBothCovered_AndNamedByTheirRepo()
    {
        var workspace = IngestionWorkspaces.NewEmptyWorkspace();
        IngestionWorkspaces.WriteFile(workspace, "src/Alpha.cs", Class("Alpha"));
        var secondRoot = Path.Combine(workspace, "second");
        Directory.CreateDirectory(secondRoot);
        new WorkspaceManager(workspace).AddRepo("second", "extra");
        File.WriteAllText(Path.Combine(secondRoot, "Beta.cs"), Class("Beta"));
        new WorkspaceIndexer().Index(workspace);
        File.Delete(Path.Combine(secondRoot, "Beta.cs"));

        var result = StructureFreshnessCheck.Check(workspace, ["demo", "extra"]);

        result.Checked.ShouldBe(2, "one pass covers every repo the answer renders, not one repo per call");
        result.Missing.ShouldBe(["extra/Beta.cs"]);
    }

    [Fact]
    public void StoreGone_LeavesThePassAtZeroCoverage_WithoutAThrow()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")));
        File.Delete(SqueezrPaths.MetaDbPath(workspace));

        StructureFreshnessResult result = default!;
        Should.NotThrow(() => result = StructureFreshnessCheck.Check(workspace, ["demo"]));

        result.Checked.ShouldBe(0);
        result.Withheld.ShouldBe(0);
        result.Skipped.ShouldBe(0);
        result.Stale.ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void RowWhosePathIsNowAChannel_CountsAsMissing_AndNothingOpensIt()
    {
        var workspace = IndexedWorkspace(("src/Empty.cs", string.Empty));
        var path = IngestionWorkspaces.Full(workspace, "src/Empty.cs");
        StoredRow(workspace, "src/Empty.cs").SizeBytes.ShouldBe(0);

        File.Delete(path);
        Assert.Equal(0, mkfifo(path, 0b100_100_100));
        File.Exists(path).ShouldBeTrue("a channel is present for stat; only the type probe tells it apart");

        var result = StructureFreshnessCheck.Check(workspace, ["demo"]);

        result.Missing.ShouldBe(["demo/src/Empty.cs"]);
        result.Checked.ShouldBe(1);
    }

    [Fact]
    public void MapRendersOnce_AndReportsTheDirtyRows_WithoutReindexingThem()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Namespace("Alpha", "Add")),
            ("src/Beta.cs", Class("Beta")));
        var rowsBefore = SnapshotRows(workspace);
        var trigramBefore = new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length;
        WriteFile(workspace, "src/Alpha.cs", Namespace("Alpha", "Sum"));
        File.Delete(IngestionWorkspaces.Full(workspace, "src/Beta.cs"));

        var renders = 0;
        var envelope = MapTool(workspace, () => renders++).Build(depth: 3).ShouldBeOfType<ToolEnvelope>();

        renders.ShouldBe(1,
            "one call, one render — the answer used to build the map a second time behind a refreshed file");
        envelope.StaleFiles.ShouldBe(["demo/src/Beta.cs", "demo/src/Alpha.cs"], StringComparer.Ordinal);
        var texts = MapTexts(envelope);
        texts.ShouldContain("int Alpha.Add(int a, int b)",
            "the map still renders the index: nothing reindexed the file it just called changed");
        texts.ShouldNotContain("int Alpha.Sum(int a, int b)");

        SnapshotRows(workspace).ShouldBe(rowsBefore,
            "no row moved, so no IndexFile ran behind this answer");
        new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length.ShouldBe(trigramBefore);
    }

    [Fact]
    public void MapRendersOnce_OnACleanIndex_Too()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Class("Alpha")),
            ("src/Beta.cs", Class("Beta")));

        var renders = 0;
        var envelope = MapTool(workspace, () => renders++).Build().ShouldBeOfType<ToolEnvelope>();

        renders.ShouldBe(1);
        envelope.StaleFiles.ShouldBeEmpty();
    }

    [Fact]
    public void Map_RendersOnceEvenWhenEveryRowIsStale()
    {
        var files = Enumerable.Range(0, 12)
            .Select(i => ($"src/Unit{i:00}.cs", Class($"Unit{i:00}")))
            .ToArray();
        var workspace = IndexedWorkspace(files);
        foreach (var (relative, _) in files)
        {
            File.AppendAllText(IngestionWorkspaces.Full(workspace, relative), "// dirty\n");
        }

        var renders = 0;
        var envelope = MapTool(workspace, () => renders++).Build().ShouldBeOfType<ToolEnvelope>();

        renders.ShouldBe(1);
        envelope.StaleFiles.Count.ShouldBe(files.Length,
            "every row of the scope is covered by the pass, not the first fifty of the render");
    }

    [Fact]
    public void StaleRowsBeyondTheSampleBound_AreCarriedAsASample_AndSaidOutLoud()
    {
        var vanished = Enumerable.Range(0, 40).Select(i => ($"src/Vanish{i:00}.cs", Class($"Vanish{i:00}"))).ToArray();
        var lagging = Enumerable.Range(0, 30).Select(i => ($"src/Lag{i:00}.cs", Class($"Lag{i:00}"))).ToArray();
        var steady = Enumerable.Range(0, 10).Select(i => ($"src/Keep{i:00}.cs", Class($"Keep{i:00}"))).ToArray();
        var workspace = IndexedWorkspace([.. vanished, .. lagging, .. steady]);
        foreach (var (relative, _) in vanished)
        {
            File.Delete(IngestionWorkspaces.Full(workspace, relative));
        }

        foreach (var (relative, _) in lagging)
        {
            File.AppendAllText(IngestionWorkspaces.Full(workspace, relative), "// past the stamp\n");
        }

        var pass = StructureFreshnessCheck.Check(workspace, ["demo"]);
        pass.Checked.ShouldBe(80);
        pass.Missing.Count.ShouldBe(vanished.Length);
        pass.Changed.Count.ShouldBe(lagging.Length,
            "the bound sits on what an answer carries, never on what this pass walked");

        var envelope = MapTool(workspace).Build().ShouldBeOfType<ToolEnvelope>();

        envelope.StaleFiles.Count.ShouldBe(StructureFreshnessCheck.StaleSampleLimit);
        envelope.StaleFiles.ShouldBe(pass.Missing.Take(25).Concat(pass.Changed.Take(25)),
            "the sample is the head of each verdict list, not a reshuffle of it");
        envelope.StaleFiles.Count(p => p.Contains("Vanish", StringComparison.Ordinal)).ShouldBe(25);
        envelope.StaleFiles.Count(p => p.Contains("Lag", StringComparison.Ordinal)).ShouldBe(25);
        SampleWarning(envelope).ShouldBe(
            "structure pass covered 80 indexed rows and found 40 missing + 30 changed; " +
            "staleFiles carries a sample of 50 of them, 20 stale rows are not listed");
    }

    [Fact]
    public void StaleClassSmallerThanItsSeat_KeepsEveryRowInTheSample_WhileTheLargerTakesTheRest()
    {
        var vanished = Enumerable.Range(0, 60).Select(i => ($"src/Vanish{i:00}.cs", Class($"Vanish{i:00}"))).ToArray();
        var lagging = Enumerable.Range(0, 2).Select(i => ($"src/Lag{i:00}.cs", Class($"Lag{i:00}"))).ToArray();
        var workspace = IndexedWorkspace([.. vanished, .. lagging]);
        foreach (var (relative, _) in vanished)
        {
            File.Delete(IngestionWorkspaces.Full(workspace, relative));
        }

        foreach (var (relative, _) in lagging)
        {
            File.AppendAllText(IngestionWorkspaces.Full(workspace, relative), "// past the stamp\n");
        }

        var envelope = MapTool(workspace).Build().ShouldBeOfType<ToolEnvelope>();

        envelope.StaleFiles.Count.ShouldBe(StructureFreshnessCheck.StaleSampleLimit);
        envelope.StaleFiles.Count(p => p.Contains("Lag", StringComparison.Ordinal)).ShouldBe(2,
            "a short class never disappears behind a long one: both claims stay visible in the payload");
        envelope.StaleFiles.Count(p => p.Contains("Vanish", StringComparison.Ordinal)).ShouldBe(48);
        SampleWarning(envelope).ShouldBe(
            "structure pass covered 62 indexed rows and found 60 missing + 2 changed; " +
            "staleFiles carries a sample of 50 of them, 12 stale rows are not listed");
    }

    [Fact]
    public void StaleRowsWithinTheBound_AreCarriedInFull_AndNothingIsSaidAboutSampling()
    {
        var files = Enumerable.Range(0, 7).Select(i => ($"src/Unit{i}.cs", Class($"Unit{i}"))).ToArray();
        var workspace = IndexedWorkspace(files);
        foreach (var (relative, _) in files.Take(4))
        {
            File.Delete(IngestionWorkspaces.Full(workspace, relative));
        }

        foreach (var (relative, _) in files.Skip(4))
        {
            File.AppendAllText(IngestionWorkspaces.Full(workspace, relative), "// past the stamp\n");
        }

        var pass = StructureFreshnessCheck.Check(workspace, ["demo"]);
        pass.Stale.Count.ShouldBe(7);

        var envelope = MapTool(workspace).Build().ShouldBeOfType<ToolEnvelope>();

        envelope.StaleFiles.ShouldBe(pass.Stale,
            "a verdict list the answer can carry whole is carried whole");
        envelope.Warnings.ShouldNotContain(w => w.Contains("staleFiles carries", StringComparison.Ordinal),
            "the sample line exists to say a list is short; saying it on a complete list is noise");
    }

    [Fact]
    public void SkippedRowsOfAnUnresolvedRepo_AreScopedToThePrefix_ThePassWasGiven()
    {
        var workspace = IngestionWorkspaces.NewEmptyWorkspace();
        IngestionWorkspaces.WriteFile(workspace, "src/Alpha.cs", Class("Alpha"));
        var secondRoot = Path.Combine(workspace, "second");
        Directory.CreateDirectory(Path.Combine(secondRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(secondRoot, "src"));
        new WorkspaceManager(workspace).AddRepo("second", "extra");
        File.WriteAllText(Path.Combine(secondRoot, "docs/Gamma.cs"), Class("Gamma"));
        File.WriteAllText(Path.Combine(secondRoot, "src/Beta.cs"), Class("Beta"));
        new WorkspaceIndexer().Index(workspace);

        var manifest = WorkspaceManifestFile.Load(workspace);
        WorkspaceManifestFile.Save(workspace, manifest with
        {
            Repos = [.. manifest.Repos.Where(r => r.Name != "extra")]
        });

        var whole = StructureFreshnessCheck.Check(workspace, ["demo", "extra"]);
        whole.Checked.ShouldBe(1);
        whole.Skipped.ShouldBe(2,
            "the store still indexes a repo the manifest dropped: those rows are coverage this pass does not have");

        var scoped = StructureFreshnessCheck.Check(workspace, ["demo", "extra"], "docs/");
        scoped.Checked.ShouldBe(0);
        scoped.Skipped.ShouldBe(1,
            "a scoped pass may not report a whole-repo denominator for the rows it never reached");
    }

    [Fact]
    public void RepoNamedInAnotherCase_StillWalksItsRows_InsteadOfCountingNothing()
    {
        var workspace = IndexedWorkspace(
            ("src/Alpha.cs", Class("Alpha")),
            ("src/Gone.cs", Class("Gone")));
        File.Delete(IngestionWorkspaces.Full(workspace, "src/Gone.cs"));

        var result = StructureFreshnessCheck.Check(workspace, ["DEMO"]);

        result.Checked.ShouldBe(2,
            "names resolve ignoring case everywhere else in this pass, so a call in another casing cannot find no repo at all");
        result.Skipped.ShouldBe(0);
        result.Missing.ShouldHaveSingleItem().ShouldEndWith("src/Gone.cs");
    }

    [Fact]
    public void MapSource_BuildsTheOnce_AndCarriesNeitherRefreshNorCap()
    {
        var toolSource = ReadSource("src/Squeezr.Cli/Mcp/Tools/RepoMapTool.cs");

        CountOccurrences(toolSource, ".Build(repo,").ShouldBe(1,
            "the map is built once per call — the second build behind it is what this task removed");
        toolSource.ShouldNotContain("CheckAndRefresh",
            customMessage: "the map no longer routes through the content guard: it neither hashes nor refreshes");
        toolSource.ShouldNotContain("IndexFile");
        toolSource.ShouldNotContain("GuardCandidateLimit");
        toolSource.ShouldContain("StructureFreshnessCheck.Check(");

        var checkSource = ReadSource("src/Squeezr.Core/Indexing/StructureFreshnessCheck.cs");
        checkSource.ShouldNotContain("SHA256", customMessage: "the structure pass reads no file content");
        checkSource.ShouldNotContain("ReadAllBytes");
        checkSource.ShouldNotContain("IndexFile");
        checkSource.ShouldNotContain("new MetaStore(",
            customMessage: "the pass opens its session through TryOpen, so an unreadable store costs zero coverage instead of an exception");
        checkSource.ShouldContain("TryOpen(");
    }

    private static RepoMapTool MapTool(string workspace, Action onRender) =>
        new(
            new McpWorkspace(workspace, workspace, null),
            new McpToolGate(),
            null,
            new ClientIdentity(),
            (root, repo, depth, maxTokens, prefix) =>
            {
                onRender();
                return new RepoMapper(root).Build(repo, depth, maxTokens, prefix);
            });

    private static RepoMapTool MapTool(string workspace) =>
        new(new McpWorkspace(workspace, workspace, null), new McpToolGate(), null, new ClientIdentity());

    private static string SampleWarning(ToolEnvelope envelope) =>
        envelope.Warnings.Single(w => w.Contains("staleFiles carries", StringComparison.Ordinal));

    private static IReadOnlyList<string> MapTexts(ToolEnvelope envelope) =>
        JsonSerializer.SerializeToElement(envelope).GetProperty("data").EnumerateArray()
            .Select(line => line.GetProperty("text").GetString()!)
            .ToList();

    private static string IndexedWorkspace(params (string Path, string Content)[] files)
    {
        var workspace = IngestionWorkspaces.NewEmptyWorkspace();
        foreach (var (relative, content) in files)
        {
            IngestionWorkspaces.WriteFile(workspace, relative, content);
        }

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string Class(string name) => $"public class {name} {{ public int Value = 1; }}\n";

    private static string Namespace(string name, string method) =>
        $"namespace Demo;\n\npublic class {name}\n{{\n    public int {method}(int a, int b)\n    {{\n        return a + b;\n    }}\n}}\n";

    private static void WriteFile(string workspace, string relativePath, string content) =>
        IngestionWorkspaces.WriteFile(workspace, relativePath, content);

    private static FileDocument StoredRow(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.FindFile(IngestionWorkspaces.RepoId(store), relativePath)!;
    }

    // the store hands the stamp back in local ticks, so the instant is read the way the pass reads it
    private static DateTime IndexedAtUtc(string workspace, string relativePath)
    {
        var stored = StoredRow(workspace, relativePath).IndexedAt;
        return stored.Kind == DateTimeKind.Utc ? stored : stored.ToUniversalTime();
    }

    private static List<string> SnapshotRows(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = IngestionWorkspaces.RepoId(store);
        return store.ListFiles(repoId)
            .Select(f => $"{f.Path}|{f.Sha256}|{f.SizeBytes}|{f.IndexedAt:O}")
            .ToList();
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(SolutionRoot(), relativePath));

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Squeezr.slnx not found above the test assembly");
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);
}
