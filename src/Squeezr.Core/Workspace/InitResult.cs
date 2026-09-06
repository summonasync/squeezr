using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public sealed record InitResult(string WorkspaceName, string RootPath)
{
    public string[] CreatedFiles { get; } =
    [
        SqueezrPaths.ManifestFileName,
        $"{SqueezrPaths.StoreDirName}/{SqueezrPaths.ConfigFileName}",
        $"{SqueezrPaths.StoreDirName}/{SqueezrPaths.MetaDbFileName}",
        $"{SqueezrPaths.StoreDirName}/{SqueezrPaths.StateFileName}",
        SqueezrPaths.ComposeFileName
    ];
}
