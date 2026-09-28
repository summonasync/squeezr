using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public sealed record InitResult(string WorkspaceName, string RootPath, string? RepairHint = null)
{
    public string[] CreatedFiles { get; } =
    [
        SqueezrPaths.ManifestPath(RootPath),
        SqueezrPaths.ConfigPath(RootPath),
        SqueezrPaths.MetaDbPath(RootPath)
    ];
}
