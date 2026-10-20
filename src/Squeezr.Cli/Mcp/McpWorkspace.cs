using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public sealed class McpWorkspace
{
    public McpWorkspace(string root, string sessionDirectory, string? embeddingsProvider)
    {
        Root = Path.GetFullPath(root);
        SessionDirectory = SessionLocation.Display(sessionDirectory);
        SessionOutsideRoot = !SessionLocation.IsWithinRoot(Root, SessionDirectory);
        EmbeddingsProvider = embeddingsProvider;
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public string Root { get; }

    public string SessionDirectory { get; }

    public string? EmbeddingsProvider { get; }

    public bool SessionOutsideRoot { get; }

    public bool IsWorkspace => File.Exists(SqueezrPaths.ManifestPath(Root));

    public void RequireWorkspace()
    {
        if (!IsWorkspace)
        {
            throw NotInitializedException();
        }
    }

    internal WorkspaceException NotInitializedException() =>
        new WorkspaceException(NotInitializedMessage(), WorkspaceException.NotInitializedCode);

    private string NotInitializedMessage() =>
        SessionOutsideRoot
            ? $"Workspace is not initialized at '{Root}' and the session directory '{SessionDirectory}' is outside it. " +
              "Do not initialize a store to work around this: this server only reads the root above."
            : RemedyText.WorkspaceNotInitialized;
}
