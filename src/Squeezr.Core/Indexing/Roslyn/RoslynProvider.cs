using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Squeezr.Core.Configuration;

namespace Squeezr.Core.Indexing.Roslyn;

public sealed class RoslynProvider : IParsingProvider
{
    private readonly RoslynSession _session;
    private readonly SqueezrConfig _config;
    private readonly List<string> _warnings = [];

    public RoslynProvider(RoslynSession session, SqueezrConfig config)
    {
        _session = session;
        _config = config;
        _warnings.AddRange(session.Warnings);
    }

    public IReadOnlyList<string> Warnings => _warnings;

    public bool CanHandle(string path, string? languageHint)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return languageHint is null
               || languageHint.Equals("csharp", StringComparison.OrdinalIgnoreCase)
               || languageHint.Equals("cs", StringComparison.OrdinalIgnoreCase);
    }

    public ParseResult Parse(FileRef file, ReadOnlySpan<byte> content)
    {
        try
        {
            var text = Encoding.UTF8.GetString(content);

            SemanticModel? model = null;
            Document? document = null;
            if (_session.Semantic)
            {
                document = _session.FindDocument(file.Path);
                model = document is null ? null : _session.GetSemanticModelAsync(document).GetAwaiter().GetResult();
            }

            var root = document is not null
                ? document.GetSyntaxRootAsync().GetAwaiter().GetResult()!
                : CSharpSyntaxTree.ParseText(text, path: file.Path).GetRoot();

            var symbols = SymbolExtractor.Extract(root, model, text);
            var refs = model is not null && document is not null
                ? ReferenceExtractor.FindAsync(symbols, document, CancellationToken.None).GetAwaiter().GetResult()
                : [];
            var chunks = SymbolChunker.Build(symbols, text, _config.Chunking);
            if (symbols.Count == 0 && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                _warnings.Add($"no symbols extracted for '{file.Path}' (syntax errors)");
            }

            return new ParseResult("csharp", symbols.Select(s => s.Symbol).ToList(), refs, chunks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _warnings.Add($"parse failed for '{file.Path}': {ex.Message}");
            return new ParseResult("csharp", [], [], []);
        }
    }
}
