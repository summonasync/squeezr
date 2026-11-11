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

        // an interface written where no type declaration names it — 'var Sink interface{ Do() error }' —
        // leaves its method_elem with no byte-stable container: an ownerless 'Do' row would be reachable
        // by name and never through member lookup, so it is refused. The split is deliberate: a named
        // interface inside a function body keeps its rows (attributed to the function's local type),
        // because there the owner exists
        if (node.Type == "method_elem" && MethodOwnerName(node, vocabulary) is null)
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

        // go's spec nodes need three tests, each one measured on the grammar
        // (TreeSitterSpikeTests.Go_*): a blank identifier is a throwaway and not a name, one spec node
        // can declare SEVERAL names, and the same kinds appear inside a function body as at package
        // level. Multi-name specs are refused rather than read one row per name: the grammar's 'name'
        // field is only the first identifier ('const A, B = 1, 2' has A as the field and B as an unnamed
        // sibling), so a per-name reading would hand both names the same span — the BUGS/022 collision
        // shape that triage has not given a span model to yet. Same deliberate drop python makes for
        // tuple targets
        "const_spec" or "var_spec" => IsGoPackageLevelBinding(node),

        // the blank leaks through every other go kind too: 'type _ int', 'func _() {}' and
        // 'func (t T) _() {}' each produced a row named '_' before this arm existed
        "type_spec" or "type_alias" or "method_elem" or "function_declaration" or "method_declaration"
            => !IsBlankGoName(node),
        _ => true,
    };

    private static bool IsBlankGoName(Node node) =>
        node.GetChildForField("name")?.Text == "_";

    // a go binding is package-level unless a function, a method or a func_literal encloses it; both
    // levels produce identical node kinds, so only the ancestor chain tells them apart. This is a
    // divergence chosen for go, not a rule inherited from the languages before it — rust's const_item and
    // static_item DO sit inside blocks and rust ships rows for them (measured: a 'const K' and a
    // 'static S' in an fn body arrive as Field rows with container 'main'). Go's block-level consts and
    // vars are locals a package reader cannot address, so they are dropped here deliberately
    private static bool IsGoPackageLevelBinding(Node node)
    {
        var targets = node.NamedChildren.Where(c => c.Type == "identifier").ToList();
        if (targets.Count != 1 || targets[0].Text == "_")
        {
            return false;
        }

        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Type is "function_declaration" or "method_declaration" or "func_literal")
            {
                return false;
            }
        }

        return true;
    }

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

            // go names every declared type through the same type_spec node, so the kind comes from what
            // the spec assigns: struct_type is a Class, interface_type an Interface, and anything else
            // ('type Mode string') is a named type with no members — Other, the same answer rust gives a
            // type_alias_item. Reading the inner node rather than the wrapper is what keeps a grouped
            // 'type ( A int; B string )' to one row per type
            "type_spec" => node.GetChildForField("type")?.Type switch
            {
                "struct_type" => SymbolKind.Class,
                "interface_type" => SymbolKind.Interface,
                _ => kind,
            },
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

    // the declaration node that owns a spec, through the group list too: a grouped 'var (' wraps its
    // members in a var_spec_list, so a spec's parent is the list and not the declaration — reading the
    // group decision off the parent alone is what made an earlier version sign a grouped member '(' and
    // take the keyword from a multi-line single declaration instead
    private static Node? GoDeclarationOwner(Node spec)
    {
        for (var current = spec.Parent; current is not null; current = current.Parent)
        {
            if (current.Type is "const_declaration" or "var_declaration" or "type_declaration")
            {
                return current;
            }
        }

        return null;
    }

    // measured group forms: the head line is just the keyword plus an opener, or the declaration carries
    // more than one spec — the child count also catches a group written on a single line
    private static bool IsGoDeclarationGroup(Node declaration) =>
        declaration.Text.Split('\n')[0].Trim() is { } head
        && (head is "const (" or "var (" or "type ("
            || declaration.NamedChildren.Count(c =>
                c.Type is "const_spec" or "var_spec" or "type_spec" or "type_alias") > 1
            || CountNestedSpecs(declaration) > 1);

    // a group lists its members inside a *_spec_list child, so the count has to look one level down
    private static int CountNestedSpecs(Node declaration) =>
        declaration.NamedChildren.Where(c => c.Type.EndsWith("_spec_list", StringComparison.Ordinal))
            .Sum(list => list.NamedChildren.Count(c =>
                c.Type is "const_spec" or "var_spec" or "type_spec" or "type_alias"));

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

        // a go spec node starts after its keyword: type_spec's own text is 'Registry struct {', not the
        // declaration line an agent searches for, so a single declaration is hoisted to its line. A GROUP
        // must not be hoisted — its head line is just 'const (' and says nothing about the member — and
        // 'group' is decided structurally: a head line that is only the keyword plus an opener, or more
        // than one spec child. 'ends with an open paren' is NOT the same predicate: 'var Registry = New('
        // is a single-spec declaration spanning lines, and hoisting it keeps the keyword (signature
        // 'var Registry = New(') while leaving the value cut at the line, as every first-line signature is
        if (node.Type is "type_spec" or "type_alias" or "const_spec" or "var_spec"
            && GoDeclarationOwner(node) is { } declaration
            && !IsGoDeclarationGroup(declaration))
        {
            target = declaration;
        }

        var firstLine = target.Text.Split('\n')[0];
        return firstLine.Trim().TrimEnd('{').Trim();
    }

    private static string? ContainerOf(Node node, SymbolVocabulary vocabulary)
    {
        // go attaches a method to the type in its receiver, not to an enclosing node: 'func (r *Registry)
        // Add' sits directly in source_file, so the owner comes out of the 'receiver' field. The string
        // is the base type without pointer or type arguments, which is exactly what the type's own row is
        // named — so symbol_lookup pairs 'Registry' with 'Add' the way it pairs a rust impl with its fn
        if (node.Type == "method_declaration" && GoMethodOwnerName(node) is { } owner)
        {
            return owner;
        }

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

    // measured receiver shapes (TreeSitterSpikeTests.Go_MethodReceivers_NameTheOwningType):
    // receiver is a parameter_list holding one parameter_declaration whose 'type' field is
    // type_identifier ('v V'), pointer_type ('*P' — no field on it, the type is its named child), or
    // generic_type ('Generic[T]', field 'type' plus a type_arguments node that names no type of its own).
    // A receiver may be unnamed ('func (*P) M()'), which is why the parameter's name is never read here
    // the type a go method belongs to, named by its receiver. Single seam for both readers of that fact:
    // ContainerOf above signs it onto the symbol row, and the reference walker prefixes it onto the
    // method's own name so an edge written inside 'func (r *Registry) Add' reads 'Registry.Add' byte for
    // byte with the row it stands in. No ancestor chain is involved either way — a method_declaration is
    // package-level in go — which is why this is a field read rather than a walk
    internal static string? GoMethodOwnerName(Node methodDeclaration) =>
        UnwrapGoType(methodDeclaration.GetChildForField("receiver")?.NamedChildren
            .FirstOrDefault(c => c.Type == "parameter_declaration")?.GetChildForField("type"));

    private static string? UnwrapGoType(Node? type) => type?.Type switch
    {
        "pointer_type" => UnwrapGoType(type.NamedChildren.LastOrDefault()),
        "generic_type" => UnwrapGoType(type.GetChildForField("type")),
        null or "" => null,
        _ => type.Text,
    };

    // answers 'is this method_elem owned by a type that has a name?', and TryCreateSymbol's refusal is
    // its ONLY caller — the container of an owned method_elem comes from the ordinary ancestor walk in
    // ContainerOf (DeclarationName finds the same type_spec/type_alias), not from here. Merging the two
    // into one owner lookup is a refactor this round deliberately does not do; the duplication to know
    // about is the boundary kind list: function/method/func_literal appear here and in
    // IsGoPackageLevelBinding, and a fifth container-ish kind would have to be added in both.
    // An interface under a type_spec or type_alias has a name to inherit — anywhere in the file,
    // including inside a function body — and under a var_spec or a parameter it has none, which is the
    // anonymous case refused
    private static string? MethodOwnerName(Node methodElem, SymbolVocabulary vocabulary)
    {
        for (var current = methodElem.Parent; current is not null; current = current.Parent)
        {
            if (current.Type is "function_declaration" or "method_declaration" or "func_literal")
            {
                return null;
            }

            if (DeclarationName(current, vocabulary) is { } name)
            {
                return name;
            }
        }

        return null;
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
