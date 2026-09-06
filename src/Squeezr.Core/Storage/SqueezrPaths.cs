namespace Squeezr.Core.Storage;

public static class SqueezrPaths
{
    public const string StoreDirName = ".squeezr";
    public const string ManifestFileName = "squeezr.workspace.json";
    public const string ConfigFileName = "config.json";
    public const string MetaDbFileName = "meta.db";
    public const string StateFileName = "state.json";
    public const string LockFileName = "index.lock";
    public const string ComposeFileName = "docker-compose.yml";
    public const string TrigramIndexFileName = "trigram.idx";

    public static string StoreDir(string root) => Path.Combine(root, StoreDirName);

    public static string ManifestPath(string root) => Path.Combine(root, ManifestFileName);

    public static string ConfigPath(string root) => Path.Combine(StoreDir(root), ConfigFileName);

    public static string MetaDbPath(string root) => Path.Combine(StoreDir(root), MetaDbFileName);

    public static string StatePath(string root) => Path.Combine(StoreDir(root), StateFileName);

    public static string TrigramIndexPath(string root) => Path.Combine(StoreDir(root), TrigramIndexFileName);

    public static string ComposePath(string root) => Path.Combine(root, ComposeFileName);
}
