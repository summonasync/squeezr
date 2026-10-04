using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SecretGuardSpanExpanderTests
{
    [Fact]
    public void Expand_ProtectedFileAlreadyInStore_RefusesWithoutRendering()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(SecretGuardFixture.EnvPath, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain(SecretGuardFixture.EnvCanary);
    }

    [Fact]
    public void Expand_ProtectedPathNeverIndexed_RefusesInsteadOfReportingMissing()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand("config/secrets.production.json", 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("not in the index");
    }

    [Fact]
    public void Expand_ProtectedPathThatIsUnreadableOnDisk_StillRefusesOnPathShape()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        File.Delete(Path.Combine(workspace, "repo", SecretGuardFixture.PemPath));

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(SecretGuardFixture.PemPath, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("unreadable");
    }

    [Fact]
    public void Expand_SuffixMatchOnProtectedFile_Refuses()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand("keys/monkey.pem", 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData(".env/")]
    [InlineData(".env\\")]
    [InlineData("deploy/keys/monkey.pem/")]
    [InlineData("demo/.env/")]
    public void Expand_ProtectedPathWithTrailingSeparator_Refuses(string file)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(file, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("is not in the index");
    }

    [Theory]
    [InlineData(".env.example/")]
    [InlineData("src/App.cs/")]
    public void Expand_OrdinaryPathWithTrailingSeparator_StillReportsMissing(string file)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(file, 1, null, 3, null, 500));

        ex.Message.ShouldNotContain("secret guard");
    }

    [Fact]
    public void Expand_ExtraPatternFile_Refuses()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(SecretGuardFixture.TokensPath, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain(SecretGuardFixture.TokensCanary);
    }

    [Fact]
    public void Expand_EnvCarveOut_StillRenders()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand(SecretGuardFixture.ExamplePath, 1, null, 0, null, 500);

        result.Span.ShouldContain($"OPENAI_API_KEY={SecretGuardFixture.ExampleCanary}");
    }

    [Fact]
    public void Expand_OrdinaryFile_StillRenders()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand(SecretGuardFixture.AppPath, 3, null, 0, null, 500);

        result.Span.ShouldContain($"    public static string Greeting => \"{SecretGuardFixture.AppCanary}\";");
    }

    [Fact]
    public void Expand_AmbiguousProtectedRequest_RefusesBeforeNamingCandidates()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace, "clone", "keys"));
        File.WriteAllText(Path.Combine(workspace, "clone", "keys", "monkey.pem"), $"{SecretGuardFixture.PemCanary} clone\n");
        var manager = new WorkspaceManager(workspace);
        manager.AddRepo("clone", "clone");
        new WorkspaceIndexer().Index(workspace);

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand("keys/monkey.pem", 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("clone:keys/monkey.pem");
    }

    [Fact]
    public void Expand_SuffixMatchingProtectedAndOrdinary_ResolvesTheOrdinaryOneAndNamesNoSecret()
    {
        var workspace = NewCrtWorkspace(
            (DeployCertPath, SecretCanary),
            (SrcCertPath, PublicCanary));
        SecretGuardFixture.SetGuardExtra(workspace, DeployGlob);

        var result = new SpanExpander(workspace).Expand(CertName, 1, null, 0, null, 500);

        result.Repo.ShouldBe("demo");
        result.File.ShouldBe(SrcCertPath);
        result.Span.ShouldHaveSingleItem().ShouldContain(PublicCanary);
        result.GuardWarnings.ShouldBeNull();
    }

    [Fact]
    public void Expand_SuffixMatchingOnlyProtected_RefusesWithoutNamingTheStoredPath()
    {
        var workspace = NewCrtWorkspace((DeployCertPath, SecretCanary));
        SecretGuardFixture.SetGuardExtra(workspace, DeployGlob);

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(CertName, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("not in the index");
        ex.Message.ShouldNotContain(DeployCertPath);
        ex.ForCli().ShouldNotContain(DeployCertPath);
    }

    [Fact]
    public void Expand_SuffixMatchingEveryCandidateProtected_RefusesWithoutListOrTally()
    {
        var workspace = NewCrtWorkspace(
            (DeployCertPath, SecretCanary),
            (TlsCertPath, SecretCanary));
        SecretGuardFixture.SetGuardExtra(workspace, DeployGlob, TlsGlob);

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand(CertName, 1, null, 3, null, 500));

        ex.ShouldNotBeOfType<ExpandInputException>();
        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("matches multiple repositories");
        ex.Message.ShouldNotContain("result(s) withheld");
        ex.Message.ShouldNotContain(DeployCertPath);
        ex.Message.ShouldNotContain(TlsCertPath);
    }

    [Fact]
    public void Expand_SuffixMatchingTwoOrdinaryAndOneProtected_ListsOnlyTheOrdinaryOnes()
    {
        var workspace = NewCrtWorkspace(
            (DeployCertPath, SecretCanary),
            ("src/one/server.crt", PublicCanary),
            ("src/two/server.crt", PublicCanary));
        SecretGuardFixture.SetGuardExtra(workspace, DeployGlob);

        var ex = Should.Throw<ExpandInputException>(
            () => new SpanExpander(workspace).Expand(CertName, 1, null, 3, null, 500));

        ex.Candidates.ShouldBe(["demo:src/one/server.crt", "demo:src/two/server.crt"]);
        ex.Message.ShouldNotContain(DeployCertPath);
        ex.ForCli().ShouldNotContain(DeployCertPath);
    }

    [Fact]
    public void Expand_RepoPrefixedSecretPath_RefusesInsteadOfReportingMissing()
    {
        var workspace = NewCrtWorkspace((DeployCertPath, SecretCanary));
        SecretGuardFixture.SetGuardExtra(workspace, DeployGlob);

        var ex = Should.Throw<WorkspaceException>(
            () => new SpanExpander(workspace).Expand($"demo/{DeployCertPath}", 1, null, 3, null, 500));

        ex.ShouldNotBeOfType<ExpandInputException>();
        ex.Message.ShouldBe(
            $"'demo/{DeployCertPath}' is protected by squeezr's built-in secret guard: never indexed, never rendered");
        ex.Message.ShouldNotContain("not in the index");
        ex.Message.ShouldNotContain("matches multiple repositories");
    }

    [Fact]
    public void Expand_SuffixMatchingTwoOrdinary_KeepsTheVerbatimAmbiguityList()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteCrtFile(workspace, "alpha", "src/Dup.cs", "public class Alpha { }\n");
        WriteCrtFile(workspace, "beta", "src/Dup.cs", "public class Beta { }\n");
        manager.AddRepo("alpha", "alpha");
        manager.AddRepo("beta", "beta");
        new WorkspaceIndexer().Index(workspace);

        var ex = Should.Throw<ExpandInputException>(
            () => new SpanExpander(workspace).Expand("src/Dup.cs", 1, null, 3, null, 500));

        ex.Message.ShouldBe(
            "Path 'src/Dup.cs' matches multiple repositories: alpha:src/Dup.cs, beta:src/Dup.cs. " +
            "Qualify the path with the repo prefix, e.g. 'alpha/src/Dup.cs'.");
        ex.ForCli().ShouldBe(
            "Path 'src/Dup.cs' matches multiple repositories: alpha:src/Dup.cs, beta:src/Dup.cs. Qualify with --repo.");
    }

    private const string CertName = "server.crt";
    private const string DeployCertPath = "deploy/server.crt";
    private const string TlsCertPath = "tls/server.crt";
    private const string SrcCertPath = "src/server.crt";
    private const string DeployGlob = "deploy/*.crt";
    private const string TlsGlob = "tls/*.crt";
    private const string SecretCanary = "SS7-CRT-SECRET-VALUE";
    private const string PublicCanary = "SS8-CRT-PUBLIC-VALUE";

    private static string NewCrtWorkspace(params (string Path, string Text)[] files)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        foreach (var (path, text) in files)
        {
            WriteCrtFile(workspace, "repo", path, text);
        }

        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteCrtFile(string workspace, string repoDir, string relativePath, string text)
    {
        var fullPath = Path.Combine(workspace, repoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }
}
