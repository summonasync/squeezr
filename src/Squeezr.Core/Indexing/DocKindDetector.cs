using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

public static class DocKindDetector
{
    public const string Code = "code";
    public const string Docs = "docs";

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
}
