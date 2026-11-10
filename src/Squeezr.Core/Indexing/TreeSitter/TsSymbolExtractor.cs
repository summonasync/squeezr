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

        // a decorator wraps its definition instead of annotating it, and the wrapper has no name field:
        // the row stays the definition's — name, kind and signature from the def line — while its span
        // grows to the whole item. The asymmetry is deliberate: a routed file is taken off the fallback
        // chunker, so a decorator line left outside every symbol span would drop out of the index
        // entirely, while a signature built from '@staticmethod' would lose the call shape agents read
        var item = vocabulary.DecoratorWrapper is { } wrapper && node.Parent?.Type == wrapper
            ? node.Parent
            : node;

        var span = new CodeSpan(item.StartPosition.Row + 1, item.EndPosition.Row + 1);
        symbol = new Symbol(
            name,
            KindOf(node, vocabulary),
            SignatureOf(node),
            span,
            ContainerOf(node, vocabulary),
            DocCommentFor(item, comments));
        return true;
    }

    internal static bool HasOwnName(Node node) => node.Type switch
    {
        // only a named function expression is a binding (module.exports = function run() {}); an
        // anonymous one has no name of its own and the identifier fallback in NameOf could pick up
        // a parameter instead
        "function_expression" => node.GetChildForField("name") is not null,
        "variable_declarator" => node.NamedChildren.Any(c => c.Type == "arrow_function"),

        // python has no const node: a module-level CONSTANT is the same 'assignment' statement as a
        // write to a local, so the shape alone does not make it a declaration
        "assignment" => IsConstantBinding(node),
        _ => true,
    };

    // two guards reject a write that is not a declaration, and they overlap on purpose:
    //  - the text test below, because UPPER_SNAKE is the convention that separates a module or class
    //    constant from a working variable, and a dotted or subscripted target carries its punctuation
    //    in the text ('CFG.MODE', 'ITEMS[0]') so it fails the same test;
    //  - NameOf's identifier-only fallback, which answers null for those same attribute and subscript
    //    forms — reached after this gate in TryCreateSymbol, so it is the second line of defence, not
    //    the first.
    // The redundancy is deliberate belt-and-braces, and it has a cost worth naming: a mutant that only
    // loosens the punctuation side of the text test survives, because NameOf then refuses the node
    // instead. What the tests pin is that no row appears, not which of the two guards refused it.
    // Tuple targets are a known drop for the same reason: 'A, B = 1, 2' carries the comma in the
    // target's text, so neither name becomes a row — unpacking a tuple of constants is not modelled as
    // declarations, and the negative is pinned in Python_TupleTargets_AreNotRows_WhileABareAnnotationIs.
    // The ancestor walk is the arm nothing else covers: an uppercase write inside a def body is the
    // same statement shape at a smaller scope, and only this walk rejects it (mutant M12).
    private static bool IsConstantBinding(Node node)
    {
        var target = node.GetChildForField("left");
        if (target is null || !IsConstName(target.Text))
        {
            return false;
        }

        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Type == "function_definition")
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConstName(string name)
    {
        var letters = 0;
        foreach (var c in name)
        {
            if (c == '_' || char.IsDigit(c))
            {
                continue;
            }

            if (!char.IsUpper(c))
            {
                return false;
            }

            letters++;
        }

        return letters > 0;
    }

    private static SymbolKind KindOf(Node node, SymbolVocabulary vocabulary)
    {
        var kind = vocabulary.Kinds[node.Type];
        return node.Type switch
        {
            "method_definition" when NameOf(node) == "constructor" => SymbolKind.Constructor,
            "function_item" or "function_signature_item"
                when InnermostContainer(node, vocabulary)?.Type is "impl_item" or "trait_item" => SymbolKind.Method,

            // python has no method node: a def is a function_definition whose enclosing definition is a
            // class, which is the same judgement rust makes through InnermostContainer
            "function_definition"
                when InnermostContainer(node, vocabulary)?.Type == "class_definition" => SymbolKind.Method,
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
