using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

internal static class StartupConfig
{
    internal static SqueezrConfig? TryRead(string workspaceRoot)
    {
        try
        {
            return ConfigFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException or ArgumentException)
        {
            return null;
        }
    }
}
