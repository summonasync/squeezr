using TreeSitter;

namespace Squeezr.Core.Indexing.TreeSitter;

internal static class TsSymbolExtractor
{
    private static readonly string[] SymbolNodeTypes =
    [
        "function_declaration",
        "function_expression",
        "method_definition",
        "class_declaration",
        "interface_declaration",
        "type_alias_declaration",
        "variable_declarator"
    ];

    public static IReadOnlyList<Symbol> Extract(Node root)
    {
        var comments = new List<Node>();
        CollectComments(root, comments);

        var results = new List<Symbol>();
        Walk(root, comments, results);
        return results;
    }

    private static void Walk(Node node, List<Node> comments, List<Symbol> results)
    {
        if (Array.IndexOf(SymbolNodeTypes, node.Type) >= 0 && TryCreateSymbol(node, comments, out var symbol))
        {
            results.Add(symbol);
        }

        foreach (var child in node.Children)
        {
            Walk(child, comments, results);
        }
    }

    private static bool TryCreateSymbol(Node node, List<Node> comments, out Symbol symbol)
    {
        symbol = null!;
        var isArrowVariable = node.Type == "variable_declarator"
                              && node.NamedChildren.Any(c => c.Type == "arrow_function");
        if (node.Type == "variable_declarator" && !isArrowVariable)
        {
            return false;
        }

        // only a named function expression is a binding (module.exports = function run() {}); an
        // anonymous one has no name of its own and the identifier fallback below could pick up a
        // parameter instead
        if (node.Type == "function_expression" && node.GetChildForField("name") is null)
        {
            return false;
        }

        var name = NameOf(node);
        if (name is null)
        {
            return false;
        }

        var kind = node.Type switch
        {
            "function_declaration" => SymbolKind.Function,
            "method_definition" when name == "constructor" => SymbolKind.Constructor,
            "method_definition" => SymbolKind.Method,
            "class_declaration" => SymbolKind.Class,
            "interface_declaration" => SymbolKind.Interface,
            "type_alias_declaration" => SymbolKind.Other,
            _ => SymbolKind.Function,
        };

        var span = new CodeSpan(node.StartPosition.Row + 1, node.EndPosition.Row + 1);
        symbol = new Symbol(
            name,
            kind,
            SignatureOf(node),
            span,
            ContainerOf(node),
            DocCommentFor(node, comments));
        return true;
    }

    private static string? NameOf(Node node)
    {
        var name = node.GetChildForField("name");
        if (name is not null)
        {
            return name.Text;
        }

        return node.NamedChildren.FirstOrDefault(c =>
            c.Type is "identifier" or "type_identifier" or "property_identifier")?.Text;
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

    private static string? ContainerOf(Node node)
    {
        var names = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var name = DeclarationName(current);
            if (name is null)
            {
                continue;
            }

            if (current.Type == "method_definition")
            {
                name += "()";
            }

            names.Insert(0, name);
        }

        return names.Count == 0 ? null : string.Join('.', names);
    }

    private static string? DeclarationName(Node node) => node.Type switch
    {
        "class_declaration" or "interface_declaration" or "function_declaration"
            or "method_definition" or "type_alias_declaration" => NameOf(node),
        "function_expression" when node.GetChildForField("name") is not null => NameOf(node),
        "variable_declarator" when node.NamedChildren.Any(c => c.Type == "arrow_function") => NameOf(node),
        _ => null,
    };

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
