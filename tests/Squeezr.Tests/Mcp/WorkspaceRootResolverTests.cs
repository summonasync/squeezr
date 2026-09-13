using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Mcp;

public sealed class WorkspaceRootResolverTests : IDisposable
{
    private const string ClaudeProjectDirEnv = "CLAUDE_PROJECT_DIR";
    private readonly string? _previousEnv = Environment.GetEnvironmentVariable(ClaudeProjectDirEnv);
    private readonly List<string> _createdDirs = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, _previousEnv);
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Resolve_ManifestInStartDir_ReturnsStartDir()
    {
        var workspace = NewWorkspaceDir();

        var result = WorkspaceRootResolver.Resolve(workspace);

        result.Root.ShouldBe(workspace);
        result.ViaClaudeProjectDir.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ManifestInStartDirAncestor_ReturnsAncestorRoot()
    {
        var workspace = NewWorkspaceDir();
        var nested = NewDir(Path.Combine(workspace, "sub", "deep"));

        var result = WorkspaceRootResolver.Resolve(nested);

        result.Root.ShouldBe(workspace);
        result.ViaClaudeProjectDir.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_NoManifestUpwardEnvPointsToWorkspace_ReturnsEnvRootViaFallback()
    {
        var workspace = NewWorkspaceDir();
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, workspace);

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(workspace);
            result.ViaClaudeProjectDir.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    [Fact]
    public void Resolve_EnvManifestInAncestorOfEnvDir_ReturnsEnvAncestorRootViaFallback()
    {
        var workspace = NewWorkspaceDir();
        var envNested = NewDir(Path.Combine(workspace, "sub"));
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, envNested);

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(workspace);
            result.ViaClaudeProjectDir.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    [Fact]
    public void Resolve_ManifestInStartDirAndEnvElsewhere_StartDirWins()
    {
        var envWorkspace = NewWorkspaceDir();
        var start = NewWorkspaceDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, envWorkspace);

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(start);
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    [Fact]
    public void Resolve_NoManifestAndNoEnv_ReturnsStartDirectory()
    {
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(start);
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    [Fact]
    public void Resolve_EnvDirectoryMissing_ReturnsStartDirectory()
    {
        var start = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, Path.Combine(start, "missing"));

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(start);
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    [Fact]
    public void Resolve_EnvSetButNoManifestAnywhere_ReturnsStartDirectory()
    {
        var start = NewDir();
        var envDir = NewDir();
        try
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, envDir);

            var result = WorkspaceRootResolver.Resolve(start);

            result.Root.ShouldBe(start);
            result.ViaClaudeProjectDir.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeProjectDirEnv, null);
        }
    }

    private string NewWorkspaceDir()
    {
        var dir = NewDir();
        File.WriteAllText(SqueezrPaths.ManifestPath(dir), """{"name":"demo","repos":[]}""");
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
