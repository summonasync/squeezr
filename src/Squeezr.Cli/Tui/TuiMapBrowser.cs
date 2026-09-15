using Spectre.Console;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiMapBrowser
{
    public static async Task RunAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'squeezr init' first.");
            console.MarkupLine("[grey]Hint: add it via \"Add repository\" in the main menu.[/]");
            return;
        }

        string root;
        try
        {
            root = NavigationContext.RequireIndex(workspaceRoot);
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
            return;
        }

        var snapshots = new Dictionary<string, RepoSnapshot>(StringComparer.OrdinalIgnoreCase);
        var back = new Stack<NavFrame>();
        NavFrame current = new RepoFrame();
        var firstEntry = true;

        while (true)
        {
            if (current is RepoFrame)
            {
                List<RepoDocument> repos;
                using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
                {
                    repos = store.ListRepos().ToList();
                }

                if (repos.Count == 0)
                {
                    console.MarkupLine("[yellow]No repositories in the index.[/] Run 'squeezr index' first.");
                    return;
                }

                if (firstEntry && repos.Count == 1)
                {
                    firstEntry = false;
                    current = new DirFrame(Snapshot(repos[0]), string.Empty);
                    continue;
                }

                var items = Enumerable.Range(0, repos.Count + 1).ToList();
                var pick = await new SelectionPrompt<int>()
                    .Title("Repositories")
                    .UseConverter(i => i < repos.Count ? repos[i].Name.EscapeMarkup() : "Back to menu")
                    .AddChoices(items)
                    .ShowAsync(console, cancellationToken);
                if (pick == repos.Count)
                {
                    return;
                }

                current = new DirFrame(Snapshot(repos[pick]), string.Empty);
                continue;
            }

            if (current is DirFrame dir)
            {
                var next = await ShowDirAsync(console, dir, cancellationToken);
                if (next is null)
                {
                    current = back.Count > 0 ? back.Pop() : new RepoFrame();
                }
                else
                {
                    back.Push(current);
                    current = next;
                }

                continue;
            }

            var fileFrame = (FileFrame)current;
            var exitToMenu = await ShowSymbolsAsync(console, fileFrame, root, cancellationToken);
            if (exitToMenu)
            {
                return;
            }

            current = back.Count > 0 ? back.Pop() : new RepoFrame();
        }

        RepoSnapshot Snapshot(RepoDocument repo)
        {
            if (snapshots.TryGetValue(repo.Name, out var cached))
            {
                return cached;
            }

            List<FileDocument> files;
            Dictionary<uint, List<SymbolDocument>> symbolsByFile;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
            {
                files = store.ListFiles(repo.Id).ToList();
                symbolsByFile = store.ListSymbolsByRepo(repo.Id)
                    .GroupBy(s => s.FileId)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }

            var snapshot = new RepoSnapshot { Repo = repo, Files = files, SymbolsByFile = symbolsByFile };
            snapshots[repo.Name] = snapshot;
            return snapshot;
        }
    }

    private static async Task<NavFrame?> ShowDirAsync(
        IAnsiConsole console, DirFrame frame, CancellationToken cancellationToken)
    {
        var (dirs, files) = ListLevel(frame.Snapshot, frame.Dir);

        var items = new List<string> { ".." };
        items.AddRange(dirs.Select(d => (d + "/").EscapeMarkup()));
        items.AddRange(files.Select(f => Path.GetFileName(f.Path).EscapeMarkup()));

        var title = frame.Dir.Length == 0
            ? $"[bold]{frame.Snapshot.Repo.Name.EscapeMarkup()}[/]"
            : $"[bold]{frame.Snapshot.Repo.Name.EscapeMarkup()}/{frame.Dir.EscapeMarkup()}[/]";
        var pick = await new SelectionPrompt<string>()
            .Title(title)
            .AddChoices(items)
            .ShowAsync(console, cancellationToken);
        if (pick == "..")
        {
            return null;
        }

        var index = items.IndexOf(pick) - 1;
        if (index < dirs.Count)
        {
            var subdir = frame.Dir.Length == 0 ? dirs[index] : frame.Dir + "/" + dirs[index];
            return new DirFrame(frame.Snapshot, subdir);
        }

        return new FileFrame(frame.Snapshot, files[index - dirs.Count]);
    }

    private static async Task<bool> ShowSymbolsAsync(
        IAnsiConsole console, FileFrame frame, string workspaceRoot, CancellationToken cancellationToken)
    {
        var title = $"{frame.Snapshot.Repo.Name}/{frame.File.Path}";
        if (!frame.Snapshot.SymbolsByFile.TryGetValue(frame.File.Id, out var symbols) || symbols.Count == 0)
        {
            console.MarkupLine($"[grey]No symbols in {title.EscapeMarkup()}.[/]");
            return false;
        }

        var ordered = symbols.OrderBy(s => s.StartLine).ToList();
        var labels = ordered
            .Select(s => $"[{s.Kind}] {s.Signature ?? s.Name}".EscapeMarkup())
            .ToList();
        var items = Enumerable.Range(0, labels.Count + 1).ToList();

        while (true)
        {
            var pick = await new SelectionPrompt<int>()
                .Title($"[bold]{title.EscapeMarkup()}[/]")
                .UseConverter(i => i == 0 ? ".." : labels[i - 1])
                .AddChoices(items)
                .ShowAsync(console, cancellationToken);
            if (pick == 0)
            {
                return false;
            }

            var symbol = ordered[pick - 1];
            var action = await TuiExpandView.RunAsync(
                console,
                workspaceRoot,
                frame.Snapshot.Repo.Name,
                frame.File.Path,
                symbol.StartLine,
                symbol.EndLine,
                ["Back to map", "Main menu"],
                cancellationToken);
            if (action == "Main menu")
            {
                return true;
            }
        }
    }

    private static (IReadOnlyList<string> Dirs, IReadOnlyList<FileDocument> Files) ListLevel(
        RepoSnapshot snapshot, string dir)
    {
        var prefix = dir.Length == 0 ? string.Empty : dir + "/";
        var subdirs = new SortedSet<string>(StringComparer.Ordinal);
        var files = new List<FileDocument>();
        foreach (var file in snapshot.Files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = file.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                files.Add(file);
            }
            else
            {
                subdirs.Add(rest[..slash]);
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (subdirs.ToList(), files);
    }

    private abstract record NavFrame;

    private sealed record RepoFrame : NavFrame;

    private sealed record DirFrame(RepoSnapshot Snapshot, string Dir) : NavFrame;

    private sealed record FileFrame(RepoSnapshot Snapshot, FileDocument File) : NavFrame;

    private sealed class RepoSnapshot
    {
        public required RepoDocument Repo { get; init; }

        public required IReadOnlyList<FileDocument> Files { get; init; }

        public required IReadOnlyDictionary<uint, List<SymbolDocument>> SymbolsByFile { get; init; }
    }
}
