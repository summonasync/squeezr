using Shouldly;
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
}
