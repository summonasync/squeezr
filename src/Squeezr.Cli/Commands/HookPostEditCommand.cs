using System.Text.Json;
using Spectre.Console.Cli;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class HookPostEditCommand : Command<HookPostEditCommand.Settings>
{
    // Same stdin ceiling as the pre-read door: an oversized payload is unreadable, not worth truncating.
    private const int MaxStdinChars = 64 * 1024;

    // The wait is not a budget for indexing — a lock held longer than this means a real pass is running
    // and its owner will carry the drift. It only rides out the short overlap between one-shot writers
    // and stays far below the harness hook timeout so a wedged store can never hang an agent pipeline.
    private static readonly TimeSpan LockWaitBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(400);

    public sealed class Settings : CommandSettings
    {
    }

    // The one exit of this door is zero. stdout stays empty unless the pass itself would print nothing
    // anyway, which it never does: a hook that answers back blocks the pipeline it watches.
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            Handle(ReadStdin());
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static void Handle(string payload)
    {
        HookPostEditInput? input;
        try
        {
            input = JsonSerializer.Deserialize<HookPostEditInput>(payload);
        }
        catch (JsonException)
        {
            return;
        }

        if (input is null || !HookPostEditToolNames.IsEditTool(input.ToolName) || input.ToolInput is null)
        {
            return;
        }

        var announced = AnnouncedPaths(input.ToolInput);
        if (announced.Count == 0)
        {
            return;
        }

        var resolution = WorkspaceRootResolver.ResolveCurrentDirectory();
        if (!resolution.IsWorkspace)
        {
            return;
        }

        var inside = PathsInsideWorkspace(resolution.RootPath, announced);
        if (inside.Count == 0)
        {
            return;
        }

        if (WatcherOwnsStore(resolution.StoreDir ?? SqueezrPaths.StoreDir(resolution.RootPath)))
        {
            return;
        }

        IndexUnderBoundedLockWait(resolution.RootPath, inside);
    }

    private static string ReadStdin()
    {
        var buffer = new char[MaxStdinChars + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = Console.In.ReadBlock(buffer, read, buffer.Length - read)) > 0)
        {
            read += chunk;
        }

        if (read <= MaxStdinChars)
        {
            return new string(buffer, 0, read);
        }

        // drain the tail so the writer never blocks on a full pipe; past the ceiling the payload is
        // treated as unreadable and the door fails open on an empty body
        var sink = new char[4096];
        while (Console.In.ReadBlock(sink, 0, sink.Length) > 0)
        {
        }

        return string.Empty;
    }

    private static List<string> AnnouncedPaths(HookPostEditToolInput toolInput)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in AnnouncedMembers(toolInput))
        {
            if (string.IsNullOrWhiteSpace(member))
            {
                continue;
            }

            var full = Path.GetFullPath(member);
            if (seen.Add(full))
            {
                paths.Add(full);
            }
        }

        return paths;
    }

    private static IEnumerable<string?> AnnouncedMembers(HookPostEditToolInput toolInput)
    {
        yield return toolInput.FilePath;
        yield return toolInput.NotebookPath;
        foreach (var fragment in toolInput.Edits ?? [])
        {
            yield return fragment.FilePath;
        }
    }

    // The workspace root plus every registered repo root — the same shapes the walk reaches. A path the
    // ordinary ingestion can never touch earns no pass; deleted files are announced as paths that no
    // longer exist, and containment is lexical, so they still qualify here like they do for the watcher.
    private static List<string> PathsInsideWorkspace(string workspaceRoot, List<string> announced)
    {
        var roots = WorkspaceRoots(workspaceRoot);
        return announced.Where(path => roots.Any(root => IsInside(root, path))).ToList();
    }

    private static List<string> WorkspaceRoots(string workspaceRoot)
    {
        var roots = new List<string> { workspaceRoot };
        foreach (var repo in WorkspaceManifestFile.Load(workspaceRoot).Repos)
        {
            roots.Add(Path.GetFullPath(
                Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path)));
        }

        return roots;
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0
               && relative != "."
               && !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith("../", StringComparison.Ordinal)
               && !relative.StartsWith("..\\", StringComparison.Ordinal);
    }

    private static bool WatcherOwnsStore(string storeDir)
    {
        var now = DateTime.UtcNow;
        foreach (var kind in (string[])[DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind])
        {
            var beat = DaemonHeartbeatReader.TryRead(storeDir, kind);
            if (beat is not null && DaemonHeartbeatReader.IsAlive(beat, now))
            {
                return true;
            }
        }

        return false;
    }

    private static void IndexUnderBoundedLockWait(string workspaceRoot, List<string> announced)
    {
        var indexer = new WorkspaceIndexer();
        var deadline = DateTime.UtcNow + LockWaitBudget;
        while (true)
        {
            try
            {
                // no embedding runtime is built here: the vectors stay pending in the store and the next
                // watcher, serve or index pass drains them — the same deferral a watcher pass runs with
                // runtime == null, and the only shape a hook may take because it must never wait on
                // provider health inline. IndexScoped and not the walk route: the door answers for the
                // announced paths only, and the one cost it accepts is opening the workspace once.
                indexer.IndexScoped(workspaceRoot, announced);
                return;
            }
            catch (WorkspaceLockedException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockRetryDelay);
            }
        }
    }
}
