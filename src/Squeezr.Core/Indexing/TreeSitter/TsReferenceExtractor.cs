using TreeSitter;

namespace Squeezr.Core.Indexing.TreeSitter;

internal static class TsReferenceExtractor
{
    public static IReadOnlyList<SymbolRef> Extract(Node root, string fromPath)
    {
        var refs = new List<SymbolRef>();
        Walk(root, fromPath, refs);
        return refs;
    }

    private static void Walk(Node node, string fromPath, List<SymbolRef> refs)
    {
        if (node.Type == "import_statement")
        {
            var module = ModuleOf(node);
            var clause = node.NamedChildren.FirstOrDefault(c => c.Type == "import_clause");
            if (clause is null)
            {
                if (module is not null)
                {
                    refs.Add(new SymbolRef(null, module, RefKind.Import, node.StartPosition.Row + 1, fromPath));
                }

                return;
            }

            foreach (var binding in ImportBindings(clause))
            {
                refs.Add(new SymbolRef(module, binding, RefKind.Import, node.StartPosition.Row + 1, fromPath));
            }

            return;
        }

        if (node.Type == "export_statement" && ModuleOf(node) is { } reexportModule)
        {
            var exportClause = node.NamedChildren.FirstOrDefault(c => c.Type == "export_clause");
            if (exportClause is null)
            {
                refs.Add(new SymbolRef(null, reexportModule, RefKind.Import, node.StartPosition.Row + 1, fromPath));
                return;
            }

            foreach (var specifier in exportClause.NamedChildren.Where(c => c.Type == "export_specifier"))
            {
                var local = specifier.GetChildForField("alias") ?? specifier.GetChildForField("name");
                if (local is not null)
                {
                    refs.Add(new SymbolRef(
                        reexportModule, local.Text, RefKind.Import, node.StartPosition.Row + 1, fromPath));
                }
            }

            return;
        }

        if (node.Type == "call_expression")
        {
            var name = CalleeName(node);
            if (name is not null && !HasAncestor(node, "import_statement"))
            {
                refs.Add(new SymbolRef(
                    null, name, RefKind.Call, node.StartPosition.Row + 1, fromPath, EnclosingName(node)));
            }
        }

        foreach (var child in node.Children)
        {
            Walk(child, fromPath, refs);
        }
    }

    private static string? ModuleOf(Node node) =>
        node.GetChildForField("source")?.Text.Trim('\'', '"', '`');

    private static IEnumerable<string> ImportBindings(Node clause)
    {
        foreach (var child in clause.NamedChildren)
        {
            switch (child.Type)
            {
                case "identifier":
                    yield return child.Text;
                    break;
                case "namespace_import":
                    var namespaceName = child.NamedChildren.FirstOrDefault(c => c.Type == "identifier");
                    if (namespaceName is not null)
                    {
                        yield return namespaceName.Text;
                    }

                    break;
                case "named_imports":
                    foreach (var specifier in child.NamedChildren.Where(c => c.Type == "import_specifier"))
                    {
                        var local = specifier.GetChildForField("alias") ?? specifier.GetChildForField("name");
                        if (local is not null)
                        {
                            yield return local.Text;
                        }
                    }

                    break;
            }
        }
    }

    private static string? CalleeName(Node call)
    {
        var fn = call.GetChildForField("function");
        if (fn is null)
        {
            return null;
        }

        return fn.Type switch
        {
            "identifier" => fn.Text,
            "member_expression" => fn.GetChildForField("property")?.Text,
            _ => null,
        };
    }

    private static bool HasAncestor(Node node, string type)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Type == type)
            {
                return true;
            }
        }

        return false;
    }

    private static string? EnclosingName(Node node)
    {
        var names = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var name = current.Type switch
            {
                "class_declaration" or "interface_declaration" or "function_declaration"
                    or "method_definition" or "type_alias_declaration" => SymbolName(current),
                "function_expression" when current.GetChildForField("name") is not null => SymbolName(current),
                "variable_declarator" when current.NamedChildren.Any(c => c.Type == "arrow_function") =>
                    SymbolName(current),
                _ => null,
            };
            if (name is not null)
            {
                names.Insert(0, name);
            }
        }

        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => $"{names[^2]}.{names[^1]}",
        };
    }

    private static string? SymbolName(Node node) =>
        node.GetChildForField("name")?.Text
        ?? node.NamedChildren.FirstOrDefault(c =>
            c.Type is "identifier" or "type_identifier" or "property_identifier")?.Text;
}
