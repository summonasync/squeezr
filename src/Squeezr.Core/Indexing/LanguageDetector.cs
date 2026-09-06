namespace Squeezr.Core.Indexing;

public static class LanguageDetector
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp",
        ["js"] = "javascript",
        ["jsx"] = "javascript",
        ["ts"] = "typescript",
        ["tsx"] = "typescript",
        ["json"] = "json",
        ["md"] = "markdown",
        ["csproj"] = "xml",
        ["sln"] = "text",
        ["yml"] = "yaml",
        ["yaml"] = "yaml",
        ["xml"] = "xml",
        ["html"] = "html",
        ["css"] = "css",
        ["sql"] = "sql",
        ["py"] = "python",
        ["sh"] = "shell",
        ["txt"] = "text"
    };

    public static string Detect(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length <= 1)
        {
            return "unknown";
        }

        return Languages.TryGetValue(extension[1..], out var language) ? language : "unknown";
    }
}
