using System.Collections.Concurrent;
using System.Text;
using Squeezr.Core.Configuration;
using TreeSitter;

namespace Squeezr.Core.Indexing.TreeSitter;

public sealed class TreeSitterProvider : IParsingProviderWithWarnings
{
    private readonly SqueezrConfig _config;
    private readonly ConcurrentBag<string> _warnings = [];

    public TreeSitterProvider(SqueezrConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<string> Warnings => _warnings.ToList();

    public bool CanHandle(string path, string? languageHint)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".js" or ".jsx" => MatchesHint(languageHint, "javascript", "js"),
            ".ts" => MatchesHint(languageHint, "typescript", "ts"),
            ".tsx" => MatchesHint(languageHint, "typescript", "tsx", "ts"),
            _ => false,
        };
    }

    public ParseResult Parse(FileRef file, ReadOnlySpan<byte> content)
    {
        var language = LanguageName(file.Path);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            using var grammar = new Language(GrammarName(file.Path));
            using var parser = new Parser(grammar);
            using var tree = parser.Parse(text)
                             ?? throw new InvalidOperationException($"tree-sitter returned no tree for '{file.Path}'");
            var root = tree.RootNode
                       ?? throw new InvalidOperationException($"tree-sitter returned no root for '{file.Path}'");
            var symbols = TsSymbolExtractor.Extract(root);
            var refs = TsReferenceExtractor.Extract(root, file.Path);
            var chunks = TsSymbolChunkBuilder.Build(symbols, text, _config.Chunking);
            return new ParseResult(language, symbols, refs, chunks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _warnings.Add($"parse failed for '{file.Path}': {ex.Message}");
            return new ParseResult(language, [], [], []);
        }
    }

    private static bool MatchesHint(string? languageHint, params string[] accepted) =>
        languageHint is null || accepted.Any(h => h.Equals(languageHint, StringComparison.OrdinalIgnoreCase));

    private static string GrammarName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ts" => "TypeScript",
            ".tsx" => "TSX",
            _ => "JavaScript",
        };

    private static string LanguageName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ts" => "typescript",
            ".tsx" => "tsx",
            ".jsx" => "javascript",
            _ => "javascript",
        };
}
