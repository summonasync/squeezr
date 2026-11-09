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
            ".js" or ".jsx" or ".mjs" or ".cjs" => MatchesHint(languageHint, "javascript", "js"),
            ".ts" => MatchesHint(languageHint, "typescript", "ts"),
            ".tsx" => MatchesHint(languageHint, "typescript", "tsx", "ts"),
            ".rs" => MatchesHint(languageHint, "rust", "rs"),
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
            var symbols = TsSymbolExtractor.Extract(root, language);
            var refs = HasReferenceRules(language) ? TsReferenceExtractor.Extract(root, file.Path) : [];
            var chunks = TsSymbolChunkBuilder.Build(symbols, text, _config.Chunking);
            return new ParseResult(language, symbols, refs, chunks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            _warnings.Add($"parse failed for '{file.Path}': {ex.Message}");
            return new ParseResult(language, [], [], []);
        }
    }

    private static bool MatchesHint(string? languageHint, params string[] accepted) =>
        languageHint is null || accepted.Any(h => h.Equals(languageHint, StringComparison.OrdinalIgnoreCase));

    // the walker models js/ts nodes: on a rust tree it reads plain `callee()` calls as refs and
    // nothing else, so rust stays out of the reference output until its own rules exist; the phase
    // reads the same predicate to decide which symbols may be matched as edge targets at all
    internal static bool HasReferenceRules(string language) =>
        language is "javascript" or "typescript" or "tsx";

    internal static string GrammarName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ts" => "TypeScript",
            ".tsx" => "TSX",
            ".rs" => "Rust",
            _ => "JavaScript",
        };

    internal static string LanguageName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ts" => "typescript",
            ".tsx" => "tsx",
            ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".rs" => "rust",
            _ => "javascript",
        };
}
