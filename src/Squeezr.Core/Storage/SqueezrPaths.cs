namespace Squeezr.Core.Storage;

public static class SqueezrPaths
{
    public const string StoreDirName = ".squeezr";
    public const string ManifestFileName = "squeezr.workspace.json";
    public const string StoreManifestFileName = "workspace.json";
    public const string ConfigFileName = "config.json";
    public const string MetaDbFileName = "meta.db";
    public const string StateFileName = "state.json";
    public const string LockFileName = "index.lock";
    public const string ComposeFileName = "docker-compose.yml";
    public const string TrigramIndexFileName = "trigram.idx";
    public const string QueriesLogFileName = "queries.jsonl";
    public const string WorkspacesDirName = "workspaces";
    public const string InfraDirName = "infra";
    public const string RegistryFileName = "workspaces.json";
    public const string RegistryLockFileName = "workspaces.lock";

    public static string StoreDir(string root) =>
        WorkspaceRegistry.TryGetStoreDir(root, out var storeDir)
            ? storeDir
            : Path.Combine(root, StoreDirName);

    public static string ManifestPath(string root) =>
        WorkspaceRegistry.TryGetStoreDir(root, out var storeDir)
            ? Path.Combine(storeDir, StoreManifestFileName)
            : Path.Combine(root, ManifestFileName);

    public static string ConfigPath(string root) => Path.Combine(StoreDir(root), ConfigFileName);

    public static string MetaDbPath(string root) => Path.Combine(StoreDir(root), MetaDbFileName);

    public static string TrigramIndexPath(string root) => Path.Combine(StoreDir(root), TrigramIndexFileName);

    public static string QueriesLogPath(string root) => Path.Combine(StoreDir(root), QueriesLogFileName);

    public static string ComposePath(string root) => Path.Combine(root, ComposeFileName);
}
