namespace Squeezr.Core.Indexing.Ignore;

public static class IgnoreSourceKey
{
    public static readonly string[] All = ["defaults", "config", "squeezrignore", "gitignore"];

    public static string Of(IgnoreSource source) => source switch
    {
        IgnoreSource.Defaults => "defaults",
        IgnoreSource.Config => "config",
        IgnoreSource.Squeezrignore => "squeezrignore",
        IgnoreSource.Gitignore => "gitignore",
        _ => source.ToString().ToLowerInvariant()
    };

    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);
}
