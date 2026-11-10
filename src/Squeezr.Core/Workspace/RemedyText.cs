using Squeezr.Core.Infra;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public static class RemedyText
{
    public const string InitCommand = "squeezr init";
    public const string IndexCommand = "squeezr index";
    public const string AddCommand = "squeezr add";
    public const string DropCommand = "squeezr drop";
    public const string ServicesUp = "squeezr services up";
    public const int ScopeHintSiblingLimit = 3;
    public const string WorkspaceNotInitialized = $"Workspace is not initialized. Run '{InitCommand}' first.";
    public const string IndexNotBuilt = $"Index is not built. Run '{IndexCommand}' first.";
    public const string BulkReadNudge =
        "This workspace is indexed by squeezr. For a read this size, code_search or symbol_lookup can locate " +
        "the relevant lines, and expand_span(file, startLine, endLine) returns just that span from the live file. " +
        "If the file or repo is not in the index, a plain Read is correct.";

    public static string AnchorInit(string root) => $"cd {ShellPath.Quote(root)} && {InitCommand}";

    public static string AnchorIndex(string root) => $"cd {ShellPath.Quote(root)} && {IndexCommand}";

    public static string AddRepo(string path, string name) => $"{AddCommand} {ShellPath.Quote(path)} --name {name}";

    public static string DropStore(string storeKey) => $"{DropCommand} --store-key {ShellPath.Quote(storeKey)}";

    public static string? ScopeBlindSiblings(IReadOnlyList<WorkspaceSiblingEntry> siblings)
    {
        if (siblings.Count == 0)
            return null;

        var named = siblings.Take(ScopeHintSiblingLimit).ToList();
        var example = named[0];
        return "this workspace holds a single repo, but the registry lists other live workspaces: " +
            string.Join(", ", named.Select(sibling => sibling.Name)) +
            " — a query whose vocabulary belongs to one of them answers with prose noise here, not with " +
            $"not-found; remedy: {AddCommand} <path> --name <repo>, e.g. {AddRepo(example.RootPath, example.Name)}, " +
            $"or serve there instead: {RebindServe(example.RootPath)}";
    }

    public static string RebindInstall(string sessionDirectory) =>
        $"cd {ShellPath.Quote(sessionDirectory)} && squeezr mcp install --client <client> --scope project";

    public static string RebindServe(string sessionDirectory) =>
        $"cd {ShellPath.Quote(sessionDirectory)} && squeezr serve";

    public static string McpInstallRepair(string client) => $"squeezr mcp install --client {client} --yes";
}
