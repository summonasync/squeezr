using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

public static class DocKindDetector
{
    public const string Code = "code";
    public const string Docs = "docs";
    public const string All = "all";

    private static readonly HashSet<string> DocsLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "markdown",
        "text",
        "json",
        "yaml",
        "toml",
        "xml",
        "unknown"
    };

    public static string Detect(string language) =>
        string.IsNullOrWhiteSpace(language) || DocsLanguages.Contains(language) ? Docs : Code;

    public static string Detect(FileDocument file) =>
        string.IsNullOrWhiteSpace(file.Kind) ? Detect(file.Language) : file.Kind;

    public static bool TryNormalizeFilter(string? value, out string? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (candidate == All)
        {
            return true;
        }

        if (candidate is not (Code or Docs))
        {
            return false;
        }

        kind = candidate;
        return true;
    }

    public static string? FilterKind(string? value) => value is Code or Docs ? value : null;
}
