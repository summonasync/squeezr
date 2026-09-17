using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public sealed class McpWorkspace
{
    public McpWorkspace(string root) => Root = Path.GetFullPath(root);

    public Guid SessionId { get; } = Guid.NewGuid();

    public string Root { get; }

    public bool IsWorkspace => File.Exists(SqueezrPaths.ManifestPath(Root));

    public void RequireWorkspace()
    {
        if (!IsWorkspace)
        {
            throw new WorkspaceException($"Workspace is not initialized. Run 'squeezr init' first.");
        }
    }
}
