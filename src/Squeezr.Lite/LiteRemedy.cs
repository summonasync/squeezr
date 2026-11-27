using Squeezr.Core.Infra;

namespace Squeezr.Lite;

public static class LiteRemedy
{
    public const string InitCommand = "sqzrlite init";
    public const string IndexCommand = "sqzrlite index";
    public const string AddCommand = "sqzrlite add";

    public const string WorkspaceNotInitialized = $"Workspace is not initialized. Run '{InitCommand}' first.";
    public const string IndexNotBuilt = $"Index is not built. Run '{IndexCommand}' first.";

    public static string AnchorInit(string root) => $"cd {ShellPath.Quote(root)} && {InitCommand}";

    public static string AnchorIndex(string root) => $"cd {ShellPath.Quote(root)} && {IndexCommand}";

    public static string AddRepo(string path, string name) => $"{AddCommand} {ShellPath.Quote(path)} --name {name}";
}
