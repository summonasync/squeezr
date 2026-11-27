using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp;

public sealed class LiteWorkspace
{
    private readonly string _sessionDirectory;
    private AnswerSubject? _subject;

    public LiteWorkspace(string root, string sessionDirectory)
    {
        Root = Path.GetFullPath(root);
        _sessionDirectory = sessionDirectory;
        SessionDirectory = SessionLocation.Display(sessionDirectory);
        SessionOutsideRoot = !SessionLocation.IsWithinRoot(Root, SessionDirectory);
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public string Root { get; }

    public string SessionDirectory { get; }

    public bool SessionOutsideRoot { get; }

    public AnswerSubject? Subject => _subject ??= AnswerSubject.Describe(Root, _sessionDirectory);

    public bool IsWorkspace => File.Exists(SqueezrPaths.ManifestPath(Root));

    public void RequireWorkspace()
    {
        if (!IsWorkspace)
        {
            throw NotInitializedException();
        }
    }

    internal WorkspaceException NotInitializedException() =>
        new(NotInitializedMessage(), WorkspaceException.NotInitializedCode);

    private string NotInitializedMessage() =>
        SessionOutsideRoot
            ? $"Workspace is not initialized at '{Root}' and the session directory '{SessionDirectory}' is outside it. " +
              "Do not initialize a store to work around this: this server only reads the root above."
            : LiteRemedy.WorkspaceNotInitialized;
}
