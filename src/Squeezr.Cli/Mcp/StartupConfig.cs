using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Mcp;

internal static class StartupConfig
{
    internal static SemanticConfig? TryRead(string workspaceRoot)
    {
        try
        {
            return SemanticConfigFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException or ArgumentException)
        {
            return null;
        }
    }
}
