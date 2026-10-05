using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Storage;

// stats names no file, so its leak is arithmetic: the size of a secret reaching the counterfactual
// denominator, and — once the purge takes the row out of the store — the same path inflating the
// "not in the index" count instead.
public class QueryTelemetrySummaryGuardTests
{
    [Fact]
    public void Summarize_ExpandOfProtectedRows_KeepsThemOutOfTheBaseline()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry(100, SecretGuardFixture.EnvPath),
            Entry(60, SecretGuardFixture.PemPath),
            Entry(120, SecretGuardFixture.AppPath),
        };

        var summary = QueryTelemetrySummary.Summarize(entries, workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(servedSize));
        summary.Savings.CounterfactualTokens.ShouldBeGreaterThan(0);
        summary.Savings.UnknownFiles.ShouldBe(0);
        summary.Savings.ActualTokens.ShouldBe(280);
        summary.Records.ShouldBe(3);
        summary.ByTool.Single(t => t.Tool == "expand").Count.ShouldBe(3);
    }

    [Fact]
    public void Summarize_ProtectedByConfigExtension_LeavesTheBaselineToo()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry(10, SecretGuardFixture.TokensPath),
            Entry(20, SecretGuardFixture.AppPath),
        };

        var summary = QueryTelemetrySummary.Summarize(entries, workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(servedSize));
        summary.Savings.UnknownFiles.ShouldBe(0);
    }

    [Fact]
    public void Summarize_ProtectedPathAlreadyPurgedFromTheStore_CountsNeitherNumber()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry(30, "config/secrets.production.json"),
            Entry(40, SecretGuardFixture.EnvPath),
        };

        var summary = QueryTelemetrySummary.Summarize(entries, workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(0);
        summary.Savings.UnknownFiles.ShouldBe(0);
        summary.Savings.ActualTokens.ShouldBe(70);
    }

    // An anchored extra ('infra/*.cs') is written against a repo root, so it matches only that shape, while the
    // ledger keeps whatever the caller typed. Qualified and absolute forms are the shapes the product itself
    // recommends: the ambiguity hint tells an agent to write 'demo/src/App.cs', and refs --json emits repo plus
    // file, so copying it yields an absolute or qualified path a second time.
    [Fact]
    public void Summarize_RepoQualifiedAnchoredExtra_KeepsSecretOutOfBothNumbers()
    {
        var workspace = NewWorkspaceWithAnchoredExtra();
        var summary = QueryTelemetrySummary.Summarize(
        [
            Entry(10, "demo/infra/VaultKeys.cs"),
            Entry(11, "demo/infra/Gone.cs"),
            Entry(20, SecretGuardFixture.AppPath),
            Entry(30, "src/Gone.cs"),
        ], workspace);

        AssertOnlyThePlainRowsRemain(workspace, summary);
    }

    [Fact]
    public void Summarize_AbsoluteAnchoredExtra_KeepsSecretOutOfBothNumbers()
    {
        var workspace = NewWorkspaceWithAnchoredExtra();
        var summary = QueryTelemetrySummary.Summarize(
        [
            Entry(10, Path.Combine(workspace, "repo", "infra", "VaultKeys.cs")),
            Entry(11, Path.Combine(workspace, "repo", "infra", "Gone.cs")),
            Entry(20, SecretGuardFixture.AppPath),
            Entry(30, "src/Gone.cs"),
        ], workspace);

        AssertOnlyThePlainRowsRemain(workspace, summary);
    }

    [Fact]
    public void Summarize_WorkspaceRelativeAnchoredExtra_KeepsSecretOutOfBothNumbers()
    {
        var workspace = NewWorkspaceWithAnchoredExtra();
        var summary = QueryTelemetrySummary.Summarize(
        [
            Entry(10, "repo/infra/VaultKeys.cs"),
            Entry(11, "repo/infra/Gone.cs"),
            Entry(20, SecretGuardFixture.AppPath),
            Entry(30, "src/Gone.cs"),
        ], workspace);

        AssertOnlyThePlainRowsRemain(workspace, summary);
    }

    // S-6 (review-064-07): once 064/T4 has purged the row, the stored-row belt has nothing left to read, and the
    // only thing standing between the ledger and 'unknownFiles' is the shape walk. Multi-segment workspace-relative
    // prefixes and '..' aliases survive only if the recorded value is resolved against every root first.
    [Fact]
    public void Summarize_NestedWorkspaceRelativeAnchoredExtra_CountsNeitherNumber()
    {
        var workspace = NewWorkspaceWithAnchoredExtra("repos/demo");
        var summary = QueryTelemetrySummary.Summarize(
        [
            Entry(10, "repos/demo/infra/VaultKeys.cs"),
            Entry(11, "repos/demo/infra/Gone.cs"),
            Entry(20, SecretGuardFixture.AppPath),
            Entry(30, "src/Gone.cs"),
        ], workspace);

        AssertOnlyThePlainRowsRemain(workspace, summary);
    }

    [Fact]
    public void Summarize_DotDotAliasedAnchoredExtra_CountsNeitherNumber()
    {
        var workspace = NewWorkspaceWithAnchoredExtra("repos/demo");
        var summary = QueryTelemetrySummary.Summarize(
        [
            Entry(10, "repos/demo/infra/../infra/VaultKeys.cs"),
            Entry(11, "repos/demo/infra/../infra/Gone.cs"),
            Entry(12, "demo/../infra/Gone.cs"),
            Entry(20, SecretGuardFixture.AppPath),
            Entry(30, "src/Gone.cs"),
        ], workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(StoredSize(workspace, SecretGuardFixture.AppPath)));
        summary.Savings.UnknownFiles.ShouldBe(1);
        summary.Records.ShouldBe(5);
    }

    private static void AssertOnlyThePlainRowsRemain(string workspace, QueryStatsSummary summary)
    {
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);
        var hiddenSize = StoredSize(workspace, "infra/VaultKeys.cs");

        // the two baselines must be tellable apart, or "counterfactual == the served one" proves nothing
        Baseline(hiddenSize).ShouldNotBe(Baseline(servedSize));
        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(servedSize));
        summary.Savings.UnknownFiles.ShouldBe(1);
        summary.Records.ShouldBe(4);
    }

    private static string NewWorkspaceWithAnchoredExtra(string repoDir = "repo")
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, repoDir.Replace('/', Path.DirectorySeparatorChar)));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo(repoDir, "demo");
        WriteFile(workspace, $"{repoDir}/{SecretGuardFixture.AppPath}", """
            public class App
            {
                public static string Greeting => "SS6-APP-VALUE";
            }
            """);
        WriteFile(workspace, $"{repoDir}/infra/VaultKeys.cs",
            "public static class VaultKeys\n{\n    public static string GetVaultSeed() => \"" +
            new string('7', 600) + "\";\n}\n");
        new WorkspaceIndexer().Index(workspace);

        SecretGuardFixture.SetGuardExtra(workspace, "infra/*.cs");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static long StoredSize(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = store.ListAllFiles().Single(f => f.Path == relativePath);
        file.LineCount.ShouldBeGreaterThan(0);
        return file.SizeBytes;
    }

    private static long Baseline(long sizeBytes) =>
        Math.Max(1, (int)Math.Ceiling(sizeBytes / 4.0 * 1.1));

    private static QueryTelemetryEntry Entry(int tokensUsed, string file) =>
        new(
            DateTime.UtcNow, Guid.NewGuid(), "expand", new { file },
            tokensUsed, false, 0, 5, false, null, "cli", null, false);
}
