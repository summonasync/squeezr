using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

public static class WorkspaceRootResolver
{
    private const string ClaudeProjectDirEnv = "CLAUDE_PROJECT_DIR";

    public static WorkspaceRootResolution Resolve(string startDirectory)
    {
        var fullStart = Path.GetFullPath(startDirectory);
        var fromStart = FindManifestRoot(fullStart);
        if (fromStart is not null)
        {
            return new WorkspaceRootResolution(fromStart, ViaClaudeProjectDir: false);
        }

        var claudeProjectDir = Environment.GetEnvironmentVariable(ClaudeProjectDirEnv);
        if (!string.IsNullOrWhiteSpace(claudeProjectDir) && Directory.Exists(claudeProjectDir))
        {
            var fromEnv = FindManifestRoot(Path.GetFullPath(claudeProjectDir));
            if (fromEnv is not null)
            {
                return new WorkspaceRootResolution(fromEnv, ViaClaudeProjectDir: true);
            }
        }

        return new WorkspaceRootResolution(fullStart, ViaClaudeProjectDir: false);
    }

    private static string? FindManifestRoot(string directory)
    {
        var current = directory;
        while (true)
        {
            if (File.Exists(SqueezrPaths.ManifestPath(current)))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return null;
            }

            current = parent.FullName;
        }
    }
}
