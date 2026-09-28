namespace Squeezr.Cli.Output;

public sealed record WorkspaceOrphanEntry(
    string Name,
    string StoreKey,
    string Class,
    string? RootPath)
{
    public string Type => "orphan";
}
