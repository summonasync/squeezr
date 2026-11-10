using TreeSitter;

namespace Squeezr.Core.Indexing.TreeSitter;

internal static class TsReferenceExtractor
{
    public static IReadOnlyList<SymbolRef> Extract(Node root, string language, string fromPath)
    {
        var references = TreeSitterLanguages.ReferencesFor(language);
        if (references is null)
        {
            return [];
        }

        var refs = new List<SymbolRef>();
        Walk(root, TreeSitterLanguages.SymbolsFor(language), references, fromPath, refs);
        return refs;
    }

    private static void Walk(
        Node node, SymbolVocabulary symbols, ReferenceVocabulary rules, string fromPath, List<SymbolRef> refs)
    {
        if (rules.ImportTypes.Contains(node.Type) && TryEmitImportRefs(node, fromPath, refs))
        {
            return;
        }

        if (rules.TypeTargetTypes.Contains(node.Type))
        {
            EmitTypeTargetRefs(node, fromPath, refs);
        }

        if (rules.CallTypes.Contains(node.Type)
            && CalleeName(node) is { } name
            && !HasAncestor(node, "import_statement"))
        {
            refs.Add(new SymbolRef(null, name, RefKind.Call, node.StartPosition.Row + 1, fromPath, EnclosingName(node, symbols)));
        }

        foreach (var child in node.Children)
        {
            Walk(child, symbols, rules, fromPath, refs);
        }
    }

    private static bool TryEmitImportRefs(Node node, string fromPath, List<SymbolRef> refs) => node.Type switch
    {
        "import_statement" => JavaScriptImportRefs(node, fromPath, refs),
        "export_statement" => JavaScriptReexportRefs(node, fromPath, refs),
        "use_declaration" => RustUseRefs(node, fromPath, refs),
        _ => false,
    };

    private static bool JavaScriptImportRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        var module = ModuleOf(node);
        var clause = node.NamedChildren.FirstOrDefault(c => c.Type == "import_clause");
        if (clause is null)
        {
            if (module is not null)
            {
                refs.Add(new SymbolRef(null, module, RefKind.Import, node.StartPosition.Row + 1, fromPath));
            }

            return true;
        }

        foreach (var binding in ImportBindings(clause))
        {
            refs.Add(new SymbolRef(module, binding, RefKind.Import, node.StartPosition.Row + 1, fromPath));
        }

        return true;
    }

    private static bool JavaScriptReexportRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        if (ModuleOf(node) is not { } reexportModule)
        {
            return false;
        }

        var exportClause = node.NamedChildren.FirstOrDefault(c => c.Type == "export_clause");
        if (exportClause is null)
        {
            refs.Add(new SymbolRef(null, reexportModule, RefKind.Import, node.StartPosition.Row + 1, fromPath));
            return true;
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

        return true;
    }

    private static bool RustUseRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        var line = node.StartPosition.Row + 1;
        if (node.GetChildForField("argument") is { } argument)
        {
            foreach (var (module, binding) in RustUseBindings(argument, null))
            {
                refs.Add(new SymbolRef(module, binding, RefKind.Import, line, fromPath));
            }
        }

        return true;
    }

    // a use clause binds names into the current module: the binding is what the walker matches
    // against symbol rows, the module is where it came from — the same pair javascript's import
    // statement carries. Nested lists keep the prefix ('serde::{serialize::Serializer}' is
    // Serializer from serde::serialize), aliases bind under the alias ('Read as Rd' is Rd), a
    // 'self' entry binds the module it sits in under its own last segment, and a glob binds the
    // module it stars — 'a::b::*' and 'b::*' both bind 'b' so one module depth cannot decide
    // whether the import resolves (the module column keeps the difference: 'a' versus nothing)
    private static IEnumerable<(string? Module, string Name)> RustUseBindings(Node clause, string? prefix)
    {
        (string? Module, string? Name) bound = clause.Type switch
        {
            "identifier" => (prefix, clause.Text),
            "scoped_identifier" => (
                Combine(prefix, clause.GetChildForField("path")?.Text),
                clause.GetChildForField("name")?.Text),
            "self" => (prefix, prefix is { } owner ? LastSegment(owner) : null),
            "use_as_clause" => (
                Combine(prefix, InnerPath(clause.GetChildForField("path"))),
                clause.GetChildForField("alias")?.Text),
            "use_wildcard" when clause.NamedChildren.FirstOrDefault() is { } globbed
                => (Combine(prefix, InnerPath(globbed)), LastSegment(globbed.Text)),
            _ => (null, null),
        };

        if (bound.Name is not null)
        {
            return [(bound.Module, bound.Name)];
        }

        return clause.Type switch
        {
            "use_list" => clause.NamedChildren.SelectMany(child => RustUseBindings(child, prefix)),
            "scoped_use_list" when clause.NamedChildren.FirstOrDefault(c => c.Type == "use_list") is { } list
                => RustUseBindings(list, Combine(prefix, clause.GetChildForField("path")?.Text)),
            _ => [],
        };
    }

    private static string LastSegment(string path)
    {
        var separator = path.LastIndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? path : path[(separator + "::".Length)..];
    }

    // the brace list's own prefix is the module; a nested path inside it extends the module
    // ('a::{b::C as D}' binds D as coming from a::b), a bare name adds nothing
    private static string? InnerPath(Node? path) =>
        path?.Type == "scoped_identifier" ? path.GetChildForField("path")?.Text : null;

    private static string? Combine(string? prefix, string? path) => (prefix, path) switch
    {
        (null, null) => null,
        (null, _) => path,
        (_, null) => prefix,
        _ => prefix + "::" + path,
    };

    private static void EmitTypeTargetRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        var line = node.StartPosition.Row + 1;
        if (node.GetChildForField("trait") is { } trait)
        {
            refs.Add(new SymbolRef(
                QualifiedPath(trait), TypeName(trait), RefKind.Implement, line, fromPath));
        }

        if (node.GetChildForField("type") is { } type)
        {
            refs.Add(new SymbolRef(null, TypeName(type), RefKind.Reference, line, fromPath));
        }
    }

    // generic targets are referenced by their bare name so the edge lands on the struct row
    // ('impl<T> app::Registry<T>' is a use of Registry), while the impl's own symbol row keeps the
    // generic arity the member lookup expects. Unwrapping is a step, not a text read: under a
    // generic sits a scoped_type_identifier, whose name column is 'Registry' and not 'app::Registry'
    private static string? TypeName(Node node) => Bare(node) switch
    {
        { Type: "reference_type" } reference when reference.GetChildForField("type") is { } inner
            => TypeName(inner),
        { Type: "scoped_type_identifier" or "scoped_identifier" } scoped => scoped.GetChildForField("name")?.Text,
        var bare => bare.Text,
    };

    private static Node Bare(Node node) =>
        node is { Type: "generic_type" } generic && generic.GetChildForField("type") is { } inner ? inner : node;

    private static string? QualifiedPath(Node node) => Bare(node) switch
    {
        { Type: "scoped_type_identifier" or "scoped_identifier" } scoped => scoped.GetChildForField("path")?.Text,
        _ => null,
    };

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

    private static string? CalleeName(Node call) => call.Type switch
    {
        // macro_invocation carries its name in the macro field, not in a function field
        "macro_invocation" => call.GetChildForField("macro")?.Text,
        _ => call.GetChildForField("function") switch
        {
            { Type: "identifier" } fn => fn.Text,
            { Type: "member_expression" } fn => fn.GetChildForField("property")?.Text,
            { Type: "field_expression" } fn => fn.GetChildForField("field")?.Text,
            { Type: "scoped_identifier" } fn => fn.GetChildForField("name")?.Text,
            _ => null,
        },
    };

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

    private static string? EnclosingName(Node node, SymbolVocabulary symbols)
    {
        var names = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (!symbols.ContainerTypes.Contains(current.Type) || !TsSymbolExtractor.HasOwnName(current))
            {
                continue;
            }

            if (TsSymbolExtractor.NameOf(current) is { } name)
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
}
