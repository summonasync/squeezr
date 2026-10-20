using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSymbolKind = Microsoft.CodeAnalysis.SymbolKind;

namespace Squeezr.Core.Indexing.Roslyn;

public sealed record ExtractedSymbol(
    Symbol Symbol,
    SyntaxNode Declaration,
    ISymbol? SemanticSymbol,
    string Signature);

public static class SymbolExtractor
{
    private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    public static IReadOnlyList<ExtractedSymbol> Extract(SyntaxNode root, SemanticModel? model, string text)
    {
        var results = new List<ExtractedSymbol>();
        foreach (var node in root.DescendantNodes().Where(IsDeclaration))
        {
            if (node is BaseFieldDeclarationSyntax field)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    Add(results, variable, model, text);
                }

                continue;
            }

            Add(results, node, model, text);
        }

        return results;
    }

    private static void Add(List<ExtractedSymbol> results, SyntaxNode declaration, SemanticModel? model, string text)
    {
        var kind = MapKind(declaration);
        var span = declaration.GetLocation().GetLineSpan();
        var codeSpan = new CodeSpan(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
        ISymbol? semantic = null;
        try
        {
            semantic = model?.GetDeclaredSymbol(declaration);
        }
        catch (ArgumentException)
        {
        }

        var name = declaration switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.Text,
            MethodDeclarationSyntax method => method.Identifier.Text,
            // generic arity stays in ContainerName; the ctor name is the bare type name
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            DestructorDeclarationSyntax dtor => $"~{dtor.Identifier.Text}",
            // the conversion target rides along so two conversions in one type stay distinct by name
            ConversionOperatorDeclarationSyntax conversion =>
                $"{conversion.ImplicitOrExplicitKeyword.ValueText} operator {conversion.Type}",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            PropertyDeclarationSyntax property => property.Identifier.Text,
            VariableDeclaratorSyntax variable => variable.Identifier.Text,
            _ => declaration.ToString().Split('(', ' ', ':')[0],
        };

        var signature = semantic?.ToDisplayString(SignatureFormat) ?? SyntaxSignature(declaration, text);
        var container = semantic?.ContainingSymbol is { Kind: not RoslynSymbolKind.Namespace } containing
            ? containing.ToDisplayString(SignatureFormat)
            : ContainerFromSyntax(declaration);
        var docComment = semantic is not null
            ? XmlDocConverter.FromSymbol(semantic)
            : declaration.GetLeadingTrivia()
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .Select(XmlDocConverter.FromTrivia)
                .FirstOrDefault(d => d is not null);

        results.Add(new ExtractedSymbol(
            new Symbol(name, kind, signature, codeSpan, container, docComment),
            declaration,
            semantic,
            signature));
    }

    private static string SyntaxSignature(SyntaxNode declaration, string text)
    {
        var start = declaration.GetLocation().SourceSpan.Start;
        var lineEnd = text.IndexOf('\n', Math.Min(start, text.Length - 1));
        var end = lineEnd < 0 ? Math.Min(start + 200, text.Length) : lineEnd;
        return text[start..end].Trim().TrimEnd('{').Trim();
    }

    private static string? ContainerFromSyntax(SyntaxNode declaration)
    {
        var names = new List<string>();
        for (var current = declaration.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseTypeDeclarationSyntax type)
            {
                names.Insert(0, type.Identifier.Text);
            }
            else if (current is MethodDeclarationSyntax method)
            {
                names.Insert(0, method.Identifier.Text + "()");
            }
        }

        return names.Count == 0 ? null : string.Join('.', names);
    }

    private static bool IsDeclaration(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax
            or MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or PropertyDeclarationSyntax
            or EventDeclarationSyntax
            or BaseFieldDeclarationSyntax;

    private static SymbolKind MapKind(SyntaxNode node) => node switch
    {
        ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax => SymbolKind.Class,
        InterfaceDeclarationSyntax => SymbolKind.Interface,
        ConstructorDeclarationSyntax => SymbolKind.Constructor,
        MethodDeclarationSyntax or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => SymbolKind.Method,
        PropertyDeclarationSyntax or EventDeclarationSyntax => SymbolKind.Property,
        BaseFieldDeclarationSyntax or VariableDeclaratorSyntax => SymbolKind.Field,
        _ => SymbolKind.Other,
    };
}
