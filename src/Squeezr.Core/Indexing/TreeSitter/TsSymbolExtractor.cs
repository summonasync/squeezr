using TreeSitter;

namespace Squeezr.Core.Indexing.TreeSitter;

internal static class TsSymbolExtractor
{
    public static IReadOnlyList<Symbol> Extract(Node root, string language)
    {
        var vocabulary = TreeSitterLanguages.SymbolsFor(language);
        var comments = new List<Node>();
        CollectComments(root, comments);

        var results = new List<Symbol>();
        Walk(root, vocabulary, comments, results);
        return results;
    }

    private static void Walk(Node node, SymbolVocabulary vocabulary, List<Node> comments, List<Symbol> results)
    {
        if (vocabulary.Kinds.ContainsKey(node.Type) && TryCreateSymbol(node, vocabulary, comments, out var symbol))
        {
            results.Add(symbol);
        }

        foreach (var child in node.Children)
        {
            Walk(child, vocabulary, comments, results);
        }
    }

    private static bool TryCreateSymbol(Node node, SymbolVocabulary vocabulary, List<Node> comments, out Symbol symbol)
    {
        symbol = null!;
        if (!HasOwnName(node))
        {
            return false;
        }

        var name = NameOf(node);
        if (name is null)
        {
            return false;
        }

        var span = new CodeSpan(node.StartPosition.Row + 1, node.EndPosition.Row + 1);
        symbol = new Symbol(
            name,
            KindOf(node, vocabulary),
            SignatureOf(node),
            span,
            ContainerOf(node, vocabulary),
            DocCommentFor(node, comments));
        return true;
    }

    internal static bool HasOwnName(Node node) => node.Type switch
    {
        // only a named function expression is a binding (module.exports = function run() {}); an
        // anonymous one has no name of its own and the identifier fallback in NameOf could pick up
        // a parameter instead
        "function_expression" => node.GetChildForField("name") is not null,
        "variable_declarator" => node.NamedChildren.Any(c => c.Type == "arrow_function"),
        _ => true,
    };

    private static SymbolKind KindOf(Node node, SymbolVocabulary vocabulary)
    {
        var kind = vocabulary.Kinds[node.Type];
        return node.Type switch
        {
            "method_definition" when NameOf(node) == "constructor" => SymbolKind.Constructor,
            "function_item" or "function_signature_item"
                when InnermostContainer(node, vocabulary)?.Type is "impl_item" or "trait_item" => SymbolKind.Method,
            _ => kind,
        };
    }

    internal static string? NameOf(Node node) => node.Type switch
    {
        "impl_item" => ImplName(node),
        _ => node.GetChildForField("name")?.Text
             ?? node.NamedChildren.FirstOrDefault(c =>
                 c.Type is "identifier" or "type_identifier" or "property_identifier")?.Text,
    };

    // tree-sitter-rust gives impl_item no name field: the row is named after its self type, qualified
    // by the trait when the block implements one — and the same string is the container of the items
    // inside, so symbol_lookup resolves an impl's members through it and the reference walker's
    // enclosing name lands on the same string
    private static string? ImplName(Node node)
    {
        var target = node.GetChildForField("type")?.Text;
        if (target is null)
        {
            return null;
        }

        return node.GetChildForField("trait")?.Text is { } trait ? $"{trait} for {target}" : target;
    }

    private static string SignatureOf(Node node)
    {
        var target = node;
        if (node.Type == "variable_declarator")
        {
            while (target.Parent is not null
                   && target.Parent.Type is "variable_declaration" or "lexical_declaration")
            {
                target = target.Parent;
            }
        }

        var firstLine = target.Text.Split('\n')[0];
        return firstLine.Trim().TrimEnd('{').Trim();
    }

    private static string? ContainerOf(Node node, SymbolVocabulary vocabulary)
    {
        var names = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var name = DeclarationName(current, vocabulary);
            if (name is null)
            {
                continue;
            }

            names.Insert(0, name);
        }

        return names.Count == 0 ? null : string.Join('.', names);
    }

    private static Node? InnermostContainer(Node node, SymbolVocabulary vocabulary)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (DeclarationName(current, vocabulary) is not null)
            {
                return current;
            }
        }

        return null;
    }

    private static string? DeclarationName(Node node, SymbolVocabulary vocabulary)
    {
        if (!vocabulary.ContainerTypes.Contains(node.Type) || !HasOwnName(node))
        {
            return null;
        }

        var name = NameOf(node);
        if (name is null)
        {
            return null;
        }

        return node.Type == "method_definition" ? name + "()" : name;
    }

    private static string? DocCommentFor(Node node, List<Node> comments)
    {
        var startRow = node.StartPosition.Row;
        var doc = comments
            .Where(c => c.EndPosition.Row == startRow - 1 || c.EndPosition.Row == startRow - 2)
            .OrderByDescending(c => c.EndPosition.Row)
            .FirstOrDefault();
        return doc is null ? null : ToPlainText(doc.Text);
    }

    private static string? ToPlainText(string raw)
    {
        if (!raw.StartsWith("/**", StringComparison.Ordinal))
        {
            return null;
        }

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var body = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == 0)
            {
                line = line["/**".Length..];
            }

            line = line.TrimEnd();
            if (line.EndsWith("*/", StringComparison.Ordinal))
            {
                line = line[..^"*/".Length];
            }

            body.Add(line.TrimStart().TrimStart('*').Trim());
        }

        while (body.Count > 0 && body[0].Length == 0)
        {
            body.RemoveAt(0);
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return body.Count == 0 ? null : string.Join("\n", body);
    }

    private static void CollectComments(Node node, List<Node> comments)
    {
        if (node.Type == "comment")
        {
            comments.Add(node);
        }

        foreach (var child in node.Children)
        {
            CollectComments(child, comments);
        }
    }
}
