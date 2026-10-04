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

    private static string Degradation(SecretGuardReader guard) =>
        guard.Warnings.ShouldNotBeNull()
            .Single(w => w.StartsWith("could not read indexing config (", StringComparison.Ordinal));
}
