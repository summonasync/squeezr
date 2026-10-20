using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing;

public class SecretGuardReaderTests
{
    [Fact]
    public void Load_ReadableConfig_SaysNothingAboutItself()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        SecretGuardReader.Load(workspace).Warnings.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"indexing": {"secretGuardExtra": 42}}""")]
    [InlineData("")]
    public void Load_UnreadableConfig_KeepsBuiltInsAndAnnouncesDeadExtensions(string corruptJson)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        SecretGuardFixture.CorruptConfig(workspace, corruptJson);

        var guard = SecretGuardReader.Load(workspace);

        guard.ExtraPatterns.ShouldBeEmpty();
        guard.IsProtected(SecretGuardFixture.EnvPath).ShouldBeTrue();
        guard.IsProtected(SecretGuardFixture.TokensPath).ShouldBeFalse();
        Degradation(guard).ShouldMatch(
            @"^could not read indexing config \(.*\); user secret-guard masks are inactive$");
    }

    [Fact]
    public void Load_MissingConfigFile_AnnouncesDeadExtensions()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        File.Delete(SqueezrPaths.ConfigPath(workspace));

        Degradation(SecretGuardReader.Load(workspace)).ShouldContain("user secret-guard masks are inactive");
    }

    [Fact]
    public void Warnings_CarriesTheWithheldCountAlongsideTheDegradation()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.CorruptConfig(workspace, "}{");
        var guard = SecretGuardReader.Load(workspace);
        guard.TryWithhold(SecretGuardFixture.EnvPath);

        var lines = guard.Warnings.ShouldNotBeNull();

        lines.ShouldContain(w => w.StartsWith("secret guard: 1 result(s) withheld", StringComparison.Ordinal));
        lines.ShouldContain(w => w.StartsWith("could not read indexing config (", StringComparison.Ordinal));
    }

    [Fact]
    public void IsProtected_TrimsTrailingSeparatorsBeforeDeciding()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        var guard = SecretGuardReader.Load(workspace);

        guard.IsProtected(".env/").ShouldBeTrue();
        guard.IsProtected(".env\\").ShouldBeTrue();
        guard.IsProtected("notes/tokens.txt/").ShouldBeTrue();
        guard.IsProtected(SecretGuardFixture.ExamplePath).ShouldBeFalse();
        guard.IsProtected(".env.example/").ShouldBeFalse();
        guard.IsProtected(SecretGuardFixture.AppPath).ShouldBeFalse();
    }

    [Fact]
    public void Warnings_PartiallyDeadExtraMask_ReportsTheDeadOneAndKeepsTheLiveHalf()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt", "**/tokens.txt/");

        var guard = SecretGuardReader.Load(workspace);

        guard.IsProtected(SecretGuardFixture.TokensPath).ShouldBeTrue();
        var lines = guard.Warnings.ShouldNotBeNull();
        lines.ShouldContain(w => w.StartsWith(DeadMaskLinePrefix, StringComparison.Ordinal));
        lines.ShouldContain(w => w.Contains("directory-only form names no file to protect", StringComparison.Ordinal));
        string.Join("|", lines).ShouldNotContain("tokens.txt");
    }

    [Fact]
    public void Warnings_DeadExtraMaskAlone_NamesTheReasonWhileTheFileStaysUnprotected()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt/");

        var guard = SecretGuardReader.Load(workspace);

        guard.IsProtected(SecretGuardFixture.TokensPath).ShouldBeFalse();
        guard.Warnings.ShouldNotBeNull().ShouldContain(w => w.StartsWith(
            $"{DeadMaskLinePrefix}1 of the user secret-guard masks in {SecretGuard.ConfiguredSourceName} "
            + "(directory-only form names no file to protect); the affected masks protect nothing",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("!**/tokens.txt", "negation would subtract, and masks only widen protection")]
    [InlineData("**/tokens[.txt", "unsupported pattern form")]
    public void Warnings_EachUnusableShape_ReportsItsOwnReason(string mask, string reason)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, mask);

        var guard = SecretGuardReader.Load(workspace);

        guard.Warnings.ShouldNotBeNull().ShouldContain(w =>
            w.StartsWith(DeadMaskLinePrefix, StringComparison.Ordinal)
            && w.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Warnings_BlankOrCommentEntries_NameNoMaskAndStaySilent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "", "# just a note");

        SecretGuardReader.Load(workspace).Warnings.ShouldBeNull();
    }

    private const string DeadMaskLinePrefix = "could not compile ";

    private static string Degradation(SecretGuardReader guard) =>
        guard.Warnings.ShouldNotBeNull()
            .Single(w => w.StartsWith("could not read indexing config (", StringComparison.Ordinal));
}
