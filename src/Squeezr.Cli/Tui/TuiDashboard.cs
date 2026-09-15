using Spectre.Console;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiDashboard
{
    public static async Task RenderAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'squeezr init' to create one.");
            return;
        }

        var status = new WorkspaceManager(workspaceRoot).GetStatus();
        var warnings = EmbeddingRuntimeFactory.TryCreate(workspaceRoot, withProvider: false, out _, out var store);
        QdrantStatus qdrant;
        using (var metaStore = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
        {
            qdrant = await QdrantStatusProbe.ProbeAsync(store, metaStore, cancellationToken);
        }

        console.MarkupLine($"[bold]Workspace:[/] {status.Name.EscapeMarkup()}");
        foreach (var repo in status.Repos)
        {
            console.MarkupLine($"  {repo.Name.EscapeMarkup()}: {repo.Files} files, {repo.Symbols} symbols, {repo.Chunks} chunks");
        }

        TuiServicesStatus.Render(console, qdrant);
        foreach (var warning in warnings)
        {
            console.MarkupLine($"[grey]{warning.EscapeMarkup()}[/]");
        }

        console.MarkupLine($"Index age: {FormatIndexAge(workspaceRoot)}");
    }

    private static string FormatIndexAge(string workspaceRoot)
    {
        var metaDbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
        if (!File.Exists(metaDbPath))
        {
            return "n/a";
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(metaDbPath);
        return age < TimeSpan.FromHours(24)
            ? $"{age:hh\\:mm\\:ss}"
            : $"{age.Days}d {age.Hours:D2}:{age.Minutes:D2}";
    }
}
