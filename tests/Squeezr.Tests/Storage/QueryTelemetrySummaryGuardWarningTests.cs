using Shouldly;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Storage;

// stats has no rows to withhold, so a dead mask there is invisible in a way it is not on the read surfaces:
// the protected file simply returns to the baseline. The only honest answer is the guard's own sentence, and
// it has to be the same sentence, not a paraphrase of it.
public class QueryTelemetrySummaryGuardWarningTests
{
    private const string DegradationPattern =
        @"^could not read indexing config \(.*\); user secret-guard masks are inactive$";

    [Fact]
    public void Summarize_BrokenExtraMask_CountsTheMaskedFileAndNamesTheDeadGuard()
    {
        var workspace = NewWorkspaceWithAConfiguredMask();
        var maskedSize = StoredSize(workspace, SecretGuardFixture.TokensPath);
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);
        Baseline(maskedSize).ShouldNotBe(Baseline(servedSize));
        SecretGuardFixture.CorruptConfig(workspace);

        var summary = QueryTelemetrySummary.Summarize(
            [Entry(SecretGuardFixture.TokensPath, 10), Entry(SecretGuardFixture.AppPath, 20)], workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(maskedSize) + Baseline(servedSize));
        summary.GuardWarnings.ShouldNotBeNull().Count.ShouldBe(1);
        Degradation(summary.GuardWarnings).ShouldNotBeNull().ShouldMatch(DegradationPattern);
    }

    [Fact]
    public void Summarize_ReadableConfigWithLiveMask_DropsTheFileAndStaysSilent()
    {
        var workspace = NewWorkspaceWithAConfiguredMask();
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);

        var summary = QueryTelemetrySummary.Summarize(
            [Entry(SecretGuardFixture.TokensPath, 10), Entry(SecretGuardFixture.AppPath, 20)], workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(servedSize));
        summary.GuardWarnings.ShouldBeNull();
    }

    [Fact]
    public void Summarize_DegradedReader_SaysWhatRepoMapSays()
    {
        var workspace = NewWorkspaceWithAConfiguredMask();
        SecretGuardFixture.CorruptConfig(workspace);

        var summary = QueryTelemetrySummary.Summarize([Entry(SecretGuardFixture.TokensPath, 10)], workspace);
        var map = new RepoMapper(workspace).Build(null, 3, 4000);

        // map withholds rows and therefore adds a tally of its own; stats only classifies. The degradation
        // sentence is the half both surfaces must speak with one voice, and it is the half this task promises.
        var mapLine = Degradation(map.GuardWarnings);
        mapLine.ShouldNotBeNull();
        Degradation(summary.GuardWarnings).ShouldBe(mapLine);
    }

    private const string DeadMaskPattern =
        @"^could not compile 1 of the user secret-guard masks in indexing\.secretGuardExtra \(directory-only form names no file to protect\); the affected masks protect nothing$";

    private const string ManifestPattern =
        @"^could not read workspace manifest \(.*\); qualified and absolute path readings are not folded for the secret guard$";

    [Fact]
    public void Summarize_DeadExtraMask_CountsTheMaskedFileAndSpeaks()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var maskedSize = StoredSize(workspace, SecretGuardFixture.TokensPath);
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt/");

        var summary = QueryTelemetrySummary.Summarize(
            [Entry(SecretGuardFixture.TokensPath, 10), Entry(SecretGuardFixture.AppPath, 20)], workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(maskedSize) + Baseline(servedSize));
        summary.GuardWarnings.ShouldNotBeNull().Count.ShouldBe(1);
        summary.GuardWarnings[0].ShouldMatch(DeadMaskPattern);
    }

    [Fact]
    public void Summarize_PartialDeadExtraMask_KeepsTheArithmeticAndStillSpeaks()
    {
        var workspace = NewWorkspaceWithAConfiguredMask();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt", "**/tokens.txt/");
        var servedSize = StoredSize(workspace, SecretGuardFixture.AppPath);

        var summary = QueryTelemetrySummary.Summarize(
            [Entry(SecretGuardFixture.TokensPath, 10), Entry(SecretGuardFixture.AppPath, 20)], workspace);

        summary.Savings.CounterfactualTokens.ShouldBe(Baseline(servedSize));
        summary.GuardWarnings.ShouldNotBeNull().Count.ShouldBe(1);
        summary.GuardWarnings[0].ShouldMatch(DeadMaskPattern);
    }

    // the reviewer's divergence, pinned as a red-before: with a manifest the qualified reading of a masked
    // file folds and the row never lands in 'not in the index'; a manifest the pass cannot read moves the
    // same row to unknownFiles and, until the reader speaks, moves it silently
    [Fact]
    public void Summarize_UnreadableManifest_TheFoldBlindnessIsAnnounced()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "infra/Gone.cs");
        var healthy = QueryTelemetrySummary.Summarize([Entry("demo/infra/Gone.cs", 10)], workspace);
        healthy.Savings.UnknownFiles.ShouldBe(0);
        healthy.GuardWarnings.ShouldBeNull();

        File.WriteAllText(SqueezrPaths.ManifestPath(workspace), "null");

        var summary = QueryTelemetrySummary.Summarize([Entry("demo/infra/Gone.cs", 10)], workspace);
        summary.Savings.UnknownFiles.ShouldBe(1);
        summary.GuardWarnings.ShouldNotBeNull().Count.ShouldBe(1);
        summary.GuardWarnings[0].ShouldMatch(ManifestPattern);
    }

    [Fact]
    public void Summarize_DeadMask_SaysWhatRepoMapSays()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt/");

        var summary = QueryTelemetrySummary.Summarize([Entry(SecretGuardFixture.TokensPath, 10)], workspace);
        var map = new RepoMapper(workspace).Build(null, 3, 4000);

        DeadMaskLine(summary.GuardWarnings).ShouldNotBeNull()
            .ShouldBe(DeadMaskLine(map.GuardWarnings).ShouldNotBeNull());
    }

    private static string? DeadMaskLine(IReadOnlyList<string>? warnings) =>
        warnings?.FirstOrDefault(w => w.StartsWith("could not compile", StringComparison.Ordinal));

    private static string? Degradation(IReadOnlyList<string>? warnings) =>
        warnings?.FirstOrDefault(
            w => w.StartsWith("could not read indexing config (", StringComparison.Ordinal));

    private static string NewWorkspaceWithAConfiguredMask()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        return workspace;
    }

    private static long StoredSize(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListAllFiles().Single(f => f.Path == relativePath).SizeBytes;
    }

    private static long Baseline(long sizeBytes) =>
        Math.Max(1, (int)Math.Ceiling(sizeBytes / 4.0 * 1.1));

    private static QueryTelemetryEntry Entry(string file, int tokensUsed) =>
        new(
            DateTime.UtcNow, Guid.NewGuid(), "expand", new { file },
            tokensUsed, false, 0, 5, false, null, "cli", null, false);
}
