using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceStoreEnvFallbackTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ClaudeProjectDirEnvironmentVariable = "CLAUDE_PROJECT_DIR";
    private readonly string? _previousHome;
    private readonly string? _previousClaudeProjectDir;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreEnvFallbackTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _previousClaudeProjectDir = Environment.GetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Resolve_WorkspaceInStartDir_ReturnsStartDir()
    {
        var workspace = NewWorkspaceDir();

        var result = WorkspaceStore.Resolve(workspace);

        result.IsWorkspace.ShouldBeTrue();
        result.RootPath.ShouldBe(Path.GetFullPath(workspace));
        result.ViaClaudeProjectDir.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_WorkspaceInStartDirAncestor_ReturnsAncestorRoot()
    {
        var workspace = NewWorkspaceDir();
        var nested = NewDir(Path.Combine(workspace, "sub", "deep"));

        var result = WorkspaceStore.Resolve(nested);

        result.IsWorkspace.ShouldBeTrue();
        result.RootPath.ShouldBe(Path.GetFullPath(workspace));
        result.ViaClaudeProjectDir.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_NoWorkspaceUpwardEnvPointsToWorkspace_ReturnsEnvRootViaFallback()
    {
        var workspace = NewWorkspaceDir();
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, workspace);

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeTrue();
            result.RootPath.ShouldBe(Path.GetFullPath(workspace));
            result.ViaClaudeProjectDir.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_EnvWorkspaceInAncestorOfEnvDir_ReturnsEnvAncestorRootViaFallback()
    {
        var workspace = NewWorkspaceDir();
        var envNested = NewDir(Path.Combine(workspace, "sub"));
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, envNested);

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeTrue();
            result.RootPath.ShouldBe(Path.GetFullPath(workspace));
            result.ViaClaudeProjectDir.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_WorkspaceInStartDirAndEnvElsewhere_StartDirWins()
    {
        var envWorkspace = NewWorkspaceDir();
        var start = NewWorkspaceDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, envWorkspace);

            var result = WorkspaceStore.Resolve(start);

            result.RootPath.ShouldBe(Path.GetFullPath(start));
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_NoWorkspaceAndNoEnv_ReturnsStartDirectory()
    {
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeFalse();
            result.RootPath.ShouldBe(Path.GetFullPath(start));
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_EnvDirectoryMissing_ReturnsStartDirectory()
    {
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, Path.Combine(start, "missing"));

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeFalse();
            result.RootPath.ShouldBe(Path.GetFullPath(start));
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_EnvSetButNoWorkspaceAnywhere_ReturnsStartDirectory()
    {
        var start = NewDir();
        var envDir = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, envDir);

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeFalse();
            result.RootPath.ShouldBe(Path.GetFullPath(start));
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Resolve_LegacyWorkspaceViaEnv_MigratesAndResolvesViaFallback()
    {
        var legacyRoot = NewDir();
        File.WriteAllText(
            Path.Combine(legacyRoot, SqueezrPaths.ManifestFileName),
            """{"name":"legacy","repos":[]}""");
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, legacyRoot);

            var result = WorkspaceStore.Resolve(start);

            result.IsWorkspace.ShouldBeTrue();
            result.RootPath.ShouldBe(Path.GetFullPath(legacyRoot));
            result.ViaClaudeProjectDir.ShouldBeTrue();
            result.MigratedFromLegacy.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, null);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable, _previousClaudeProjectDir);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string NewWorkspaceDir()
    {
        var dir = NewDir();
        new WorkspaceManager(dir).Init("demo");
        return dir;
    }

    private string NewDir(string? path = null)
    {
        var dir = Path.GetFullPath(path ?? TestDirs.NewTempDir());
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return dir;
    }
}
