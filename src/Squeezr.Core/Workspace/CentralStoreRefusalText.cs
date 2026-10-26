using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public static class CentralStoreRefusalText
{
    public static string Migration(string storeDir) =>
        $"Refusing to migrate '{storeDir}': it is the central squeezr store, not a legacy in-project store. " +
        $"{Homes()} Nothing was moved, renamed or deleted. " +
        $"Re-run with the override unset: env -u {SqueezrHome.HomeEnvironmentVariable} <command>.";

    public static string StructuralMigration(string storeDir) =>
        $"Refusing to migrate '{storeDir}': it carries the central squeezr store signature (a registry file or " +
        $"a workspaces directory), not a plain legacy in-project store. {Homes()} Nothing was moved, renamed or " +
        "deleted. To relocate it deliberately run: squeezr workspace migrate '<root>' --yes.";

    public static string InitAt(string root) =>
        $"Cannot initialize a workspace at '{root}': the directory holds the central squeezr store. " +
        $"{Homes()} Nothing was created or changed. " +
        "Initialize the workspace at its project root instead.";

    public static string Drop(string root, string storeDir) =>
        $"Refusing to drop '{root}': '{storeDir}' is the central squeezr store, not a workspace store. " +
        $"{Homes()} Nothing was deleted.";

    private static string Homes()
    {
        var overrideValue = Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable);
        var overrideFact = string.IsNullOrWhiteSpace(overrideValue)
            ? $"{SqueezrHome.HomeEnvironmentVariable} is not set."
            : $"{SqueezrHome.HomeEnvironmentVariable}='{overrideValue}' is in effect.";
        return $"Default home '{SqueezrHome.DefaultHome()}'; resolved home '{SqueezrHome.Resolve()}'; {overrideFact}";
    }
}
