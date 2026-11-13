using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public sealed class McpWorkspace
{
    private readonly string _sessionDirectory;
    private AnswerSubject? _subject;

    public McpWorkspace(string root, string sessionDirectory, string? embeddingsProvider)
    {
        Root = Path.GetFullPath(root);
        _sessionDirectory = sessionDirectory;
        SessionDirectory = SessionLocation.Display(sessionDirectory);
        SessionOutsideRoot = !SessionLocation.IsWithinRoot(Root, SessionDirectory);
        EmbeddingsProvider = embeddingsProvider;
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public string Root { get; }

    public string SessionDirectory { get; }

    public string? EmbeddingsProvider { get; }

    public bool SessionOutsideRoot { get; }

    // Which tree this session's answers describe, read once: a serve process binds one store for its whole life,
    // and the answer to "is this the tree the caller stands in" cannot change under it without the caller's own
    // session moving. Null for the single-root case — and null is what keeps every envelope of an ordinary
    // session the JSON it always was (035).
    public AnswerSubject? Subject => _subject ??= AnswerSubject.Describe(Root, _sessionDirectory);

    // The host writes this when the watch claim sends the session to observer duty; the tool gate reads it once,
    // so the agent learns the standing on its first answer without paying the same line on every call.
    public string? WatchNotice { get; internal set; }

    internal string? TakeWatchNotice()
    {
        var notice = WatchNotice;
        WatchNotice = null;
        return notice;
    }

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
