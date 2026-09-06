using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynSymbolKind = Microsoft.CodeAnalysis.SymbolKind;

namespace Squeezr.Core.Indexing.Roslyn;

public static class ReferenceExtractor
{
    public static async Task<IReadOnlyList<SymbolRef>> FindAsync(
        IReadOnlyList<ExtractedSymbol> declared,
        Document document,
        CancellationToken cancellationToken)
    {
        var refs = new List<SymbolRef>();
        var roots = new Dictionary<Document, SyntaxNode?>();
        var scope = ImmutableHashSet.CreateRange(document.Project.Documents);

        foreach (var extracted in declared)
        {
            var symbol = extracted.SemanticSymbol;
            if (symbol is null || symbol.Kind == RoslynSymbolKind.Local || symbol.IsImplicitlyDeclared)
            {
                continue;
            }

            var locations = await SymbolFinder
                .FindReferencesAsync(symbol, document.Project.Solution, scope, cancellationToken)
                .ConfigureAwait(false);
            foreach (var referenced in locations)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.SourceTree is null)
                    {
                        continue;
                    }

                    var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                    var doc = document.Project.Solution.GetDocument(location.Location.SourceTree);
                    if (doc is null || !roots.TryGetValue(doc, out var root))
                    {
                        root = doc is null ? null : await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                        if (doc is not null)
                        {
                            roots[doc] = root;
                        }
                    }

                    var kind = Classify(root, location, symbol);
                    refs.Add(new SymbolRef(
                        null,
                        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        kind,
                        line,
                        doc?.FilePath));
                }
            }
        }

        return refs;
    }

    private static RefKind Classify(SyntaxNode? root, ReferenceLocation location, ISymbol target)
    {
        var node = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is UsingDirectiveSyntax)
            {
                return RefKind.Import;
            }

            if (current is BaseListSyntax)
            {
                return target is INamedTypeSymbol { TypeKind: TypeKind.Interface } ? RefKind.Implement : RefKind.Inherit;
            }

            if (current is InvocationExpressionSyntax)
            {
                return RefKind.Call;
            }
        }

        return RefKind.Reference;
    }
}
