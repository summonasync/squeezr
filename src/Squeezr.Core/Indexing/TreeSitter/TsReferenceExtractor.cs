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
        if (rules.ImportTypes.Contains(node.Type) && TryEmitImportRefs(node, rules, fromPath, refs))
        {
            return;
        }

        if (rules.TypeTargetTypes.Contains(node.Type))
        {
            EmitTypeTargetRefs(node, rules, fromPath, refs);
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

    // the emitter is chosen by the language row, not by the node kind: python and javascript both write an
    // import node named import_statement and the fields under it have nothing in common, so a kind-keyed
    // switch would run javascript's reader over a python import and return 'no source field' — zero
    // references, no warning, and every smoke test green. No default arm here on purpose: the import
    // reader and the type-target reader below are switch expressions over ReferenceReader, so adding a
    // language to that enum makes the compiler name this line (CS8509) instead of letting the new grammar
    // quietly read javascript's fields
    //
    // the price, paid on purpose: an enum switch expression without a default arm also reports CS8524
    // forever, because a reader value could in principle be an unnamed one — the compiler names the value
    // just past the last declared reader — that no named arm covers, so this line and the type-target line
    // below are two permanent warnings in
    // every build (the solution baseline moved 37 -> 39 with them). Do not 'fix' that with a default arm:
    // a default swallows the one compile-time signal this design exists to get, and a new language would
    // silently read somebody else's node fields again — the day a reader value really arrives unnamed, the
    // switch throws at runtime and the provider's catch turns it into a visible fallback plus a warning
    private static bool TryEmitImportRefs(Node node, ReferenceVocabulary rules, string fromPath, List<SymbolRef> refs) =>
        rules.Reader switch
        {
            ReferenceReader.Python => PythonImportRefs(node, fromPath, refs),
            ReferenceReader.Go => GoImportRefs(node, fromPath, refs),
            ReferenceReader.Rust => RustUseRefs(node, fromPath, refs),
            ReferenceReader.JsTs => node.Type switch
            {
                "import_statement" => JavaScriptImportRefs(node, fromPath, refs),
                "export_statement" => JavaScriptReexportRefs(node, fromPath, refs),
                _ => false,
            },
        };

    // python imports carry (module column, binding), and the binding rule is python's own, not borrowed
    // from another grammar: 'import a.b.c' binds the TOP package — the file may then write a.b.c, but it
    // never has a bare 'c' — while 'import a.b.c as d' binds d and 'from a.b import c' binds c. The binding
    // is therefore the shortest name the file can now type, and the module column keeps the path it was
    // reached through: the whole path for a plain dotted import ('os.path'), its prefix when an alias names
    // the leaf ('a.b' for 'a.b.c as d'), the from-module for a from-import, and '.' or '..pkg' verbatim for
    // a relative one — which is what javascript already stores in the same column for './util'.
    // 'from x import *' types no name at all, so it anchors on the starred module the way rust's globbed
    // use does: the module's last segment as the binding, the path in the column. That is the one place the
    // two grammars share a rule, and it is a deliberate mirror rather than a python reading of CPython
    private static bool PythonImportRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        var line = node.StartPosition.Row + 1;
        if (node.Type == "import_statement")
        {
            // 'import os, sys' repeats the name field, so the bindings are read from the CHILDREN — a
            // field read would index 'os' and quietly lose 'sys'
            foreach (var child in node.NamedChildren.Where(IsPythonBindingNode))
            {
                var (moduleColumn, binding) = PythonPathParts(child);
                if (binding is not null)
                {
                    refs.Add(new SymbolRef(moduleColumn, binding, RefKind.Import, line, fromPath));
                }
            }

            return true;
        }

        var module = node.GetChildForField("module_name");
        var moduleText = module?.Text;
        if (node.NamedChildren.Any(c => c.Type == "wildcard_import"))
        {
            if (moduleText is not null)
            {
                refs.Add(new SymbolRef(
                    moduleText, DottedLastSegment(moduleText), RefKind.Import, line, fromPath));
            }

            return true;
        }

        // the module of 'from a.b import c' is itself a dotted_name child, so the binding loop has to
        // skip it by position or 'a.b' arrives as a binding as well as a module
        foreach (var child in node.NamedChildren.Where(c =>
                   IsPythonBindingNode(c) && !SameNode(c, module)))
        {
            var binding = PythonBindingName(child);
            if (binding is not null)
            {
                refs.Add(new SymbolRef(moduleText, binding, RefKind.Import, line, fromPath));
            }
        }

        return true;
    }

    // go's import shapes read from go: a spec's path is the identity of the package it pulls in, and the
    // name the file can type is the LAST element of that path unless the spec says otherwise —
    // 'import "net/http"' gives 'http'. An alias (a package_identifier in the name field) wins outright;
    // a dot-import ('. "strings"') binds every export of the package under their own names, so no single
    // name can be recorded and the row anchors on the package the way a rust globbed use and a python
    // 'from x import *' do; and a blank name ('_ "database/sql"') is a side-effect import that binds
    // NOTHING, so it emits no row at all — the same refusal go's symbol table applies to the blank
    // identifier. Unlike python, where a dotted path binds its FIRST element, the module column here
    // keeps the whole quoted path because that path is what the import statement actually resolved
    private static bool GoImportRefs(Node node, string fromPath, List<SymbolRef> refs)
    {
        var line = node.StartPosition.Row + 1;
        foreach (var spec in GoImportSpecs(node))
        {
            var path = TrimImportPath(spec.GetChildForField("path")?.Text);
            if (path is null)
            {
                continue;
            }

            var declared = spec.GetChildForField("name");
            if (declared?.Type == "blank_identifier")
            {
                continue;
            }

            var binding = declared?.Type switch
            {
                null => GoPackageOf(path),
                "package_identifier" => declared.Text,
                "dot" => GoPackageOf(path),
                _ => null,
            };
            if (binding is not null)
            {
                refs.Add(new SymbolRef(path, binding, RefKind.Import, line, fromPath));
            }
        }

        return true;
    }

    // a single 'import "os"' hangs its spec off the declaration directly; the parenthesised form wraps
    // them in an import_spec_list
    private static IEnumerable<Node> GoImportSpecs(Node importDeclaration) =>
        importDeclaration.NamedChildren.Any(c => c.Type == "import_spec_list")
            ? importDeclaration.NamedChildren.Where(c => c.Type == "import_spec_list")
                .SelectMany(list => list.NamedChildren.Where(c => c.Type == "import_spec"))
            : importDeclaration.NamedChildren.Where(c => c.Type == "import_spec");

    private static string? TrimImportPath(string? quoted) =>
        quoted is null ? null : quoted.Trim('"');

    private static string GoPackageOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    // go's type relations, all read from the positions that name a type rather than define one:
    //  - a struct field with no name field EMBEDS its type — go's inheritance — and gets RefKind.Inherit
    //    the way a python base class does; a named field is a use, RefKind.Reference. A field whose name
    //    is the blank discards the slot: it binds nothing and embeds nothing, so no edge is signed for it
    //    (the symbol side refuses the same blank — B-1). Two names for one type ('a, b Base') is ONE edge,
    //    not two: the grammar's name field is only the first identifier, and a per-name reading would sign
    //    the same span twice — the multi-name refusal the symbol row already makes (B-2), applied to a use
    //  - an interface's embedded element is inheritance too, read as a type_elem whose PARENT is the
    //    interface_type: measured over a file with both an embedded element and a generic instantiation
    //    ('Store' in an interface, 'int' in 'Generic[int]'), tree-sitter-go spells both as type_elem, so
    //    the parent is the only thing separating 'embeds a type' from 'instantiates with one' — and the
    //    instantiation argument is an aggregate-position use, out of scope like the others above
    //  - a type_spec or type_alias whose assigned type is another named type is a use, while
    //    'type T struct{…}' and 'type T interface{…}' DEFINE and produce no edge of their own — their
    //    fields and elements are read on their own account.
    // Deliberately out of scope, and pinned as a fence rather than left implied: element types inside
    // aggregates ('[]Measure', 'map[string]Item', 'chan Signal') and inside function types, because a
    // field of type 'map[string]int' is not a use of 'int' in the sense a reader of the struct wants, and
    // unwrapping them would fill the pool with builtin names. Composite literals ('Store{}', the shape go
    // reaches for where rust writes 'Registry { … }' and javascript 'new Ledger()') stay edge-less for the
    // same reason the other two do — construction is not a reference
    private static List<SymbolRef> GoTypeTargetRows(Node node, string fromPath)
    {
        var rows = new List<SymbolRef>();
        var line = node.StartPosition.Row + 1;
        switch (node.Type)
        {
            case "field_declaration":
                var name = node.GetChildForField("name");
                if (name?.Text == "_")
                {
                    break;
                }

                // no name field at all means the field IS the embedded type
                AddGoTypeTarget(rows, node.GetChildForField("type"),
                    name is null ? RefKind.Inherit : RefKind.Reference, line, fromPath);
                break;
            case "type_elem" when node.Parent?.Type == "interface_type":
                AddGoTypeTarget(rows, node.NamedChildren.FirstOrDefault(), RefKind.Inherit, line, fromPath);
                break;
            case "type_spec" or "type_alias":
                // 'type T struct{}' defines; 'type T Measure' and 'type Alias = Store' name something else
                if (node.GetChildForField("type")?.Type is not ("struct_type" or "interface_type"))
                {
                    AddGoTypeTarget(rows, node.GetChildForField("type"), RefKind.Reference, line, fromPath);
                }

                break;
        }

        return rows;
    }

    private static void AddGoTypeTarget(List<SymbolRef> rows, Node? type, RefKind kind, int line, string fromPath)
    {
        var (module, name) = GoTypeName(type);
        if (name is null)
        {
            return;
        }

        rows.Add(new SymbolRef(module, name, kind, line, fromPath));
    }

    // the nameable shapes, per the measured fields: type_identifier names itself, qualified_type keeps
    // its package in the module column ('http.Handler' → package 'http', type 'Handler'), pointer_type
    // wraps a named type through its last named child (pointer_type has no 'type' field, measured) and
    // generic_type through its 'type' field. Anything else — the aggregates and function types — is
    // reported as nameless so no edge is invented for it
    private static (string? Module, string? Name) GoTypeName(Node? type) => type?.Type switch
    {
        "type_identifier" => (null, type.Text),
        "qualified_type" when type.GetChildForField("name") is { } qualified =>
            (type.NamedChildren.FirstOrDefault()?.Text, qualified.Text),
        "pointer_type" => GoTypeName(type.NamedChildren.LastOrDefault()),
        "generic_type" => GoTypeName(type.GetChildForField("type")),
        _ => (null, null),
    };

    private static bool IsPythonBindingNode(Node child) =>
        child.Type is "dotted_name" or "aliased_import";

    private static bool SameNode(Node left, Node? right) =>
        right is not null && left.StartIndex == right.StartIndex && left.EndIndex == right.EndIndex;

    // (module column, binding) for one 'import a.b.c [as d]' child
    private static (string? Module, string? Name) PythonPathParts(Node child)
    {
        if (child.Type == "aliased_import")
        {
            var path = child.GetChildForField("name")?.Text;
            return (path is null ? null : DottedPrefix(path), child.GetChildForField("alias")?.Text);
        }

        var text = child.Text;
        // 'import os' names the module it pulled in; 'import os.path' names the package 'os' and reaches
        // the deeper module behind it — for the un-aliased form the binding is the FIRST segment, never
        // the leaf, and the column keeps the whole path the statement wrote
        return text.Contains('.', StringComparison.Ordinal)
            ? (text, DottedFirstSegment(text))
            : (null, text);
    }

    private static string DottedFirstSegment(string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? path : path[..dot];
    }

    private static string? PythonBindingName(Node child) => child.Type switch
    {
        "aliased_import" => child.GetChildForField("alias")?.Text ?? child.GetChildForField("name")?.Text,
        "dotted_name" => DottedLastSegment(child.Text),
        _ => null,
    };

    private static string? DottedPrefix(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? null : path[..dot];
    }

    private static string DottedLastSegment(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

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

    // one switch expression per reader, no default: a language added to ReferenceReader has to say how its
    // declarations name a target before it compiles quietly, instead of falling through into rust's 'trait'
    // and 'type' field reads — the same silent-wrong class as the import dispatch above
    private static void EmitTypeTargetRefs(Node node, ReferenceVocabulary rules, string fromPath, List<SymbolRef> refs)
    {
        refs.AddRange(rules.Reader switch
        {
            ReferenceReader.Python => PythonClassTargetRows(node, fromPath),
            ReferenceReader.Go => GoTypeTargetRows(node, fromPath),
            ReferenceReader.Rust => RustImplTargetRows(node, fromPath),
            // javascript declares no type targets (its TypeTargetTypes is empty), and saying so is what
            // keeps the row honest in the home rather than inherited from an arm it never reaches
            ReferenceReader.JsTs => [],
        });
    }

    private static List<SymbolRef> RustImplTargetRows(Node node, string fromPath)
    {
        var line = node.StartPosition.Row + 1;
        var rows = new List<SymbolRef>();
        if (node.GetChildForField("trait") is { } trait)
        {
            rows.Add(new SymbolRef(
                QualifiedPath(trait), TypeName(trait), RefKind.Implement, line, fromPath));
        }

        if (node.GetChildForField("type") is { } type)
        {
            rows.Add(new SymbolRef(null, TypeName(type), RefKind.Reference, line, fromPath));
        }

        return rows;
    }

    // a python class names its bases in the 'superclasses' field, which is an argument_list — the same node
    // kind go gives a call's arguments, so it is only ever read through this python row. Keyword children
    // are configuration, not inheritance: 'class Widget(Base, metaclass=ABCMeta)' inherits Base and asks
    // ABCMeta to build it, and an edge for the second would read as a parent class in symbol_lookup
    private static List<SymbolRef> PythonClassTargetRows(Node node, string fromPath)
    {
        var rows = new List<SymbolRef>();
        if (node.GetChildForField("superclasses") is not { } bases)
        {
            return rows;
        }

        var line = node.StartPosition.Row + 1;
        foreach (var baseNode in bases.NamedChildren.Where(c => c.Type != "keyword_argument"))
        {
            var name = PythonTargetName(baseNode);
            if (name is not null)
            {
                rows.Add(new SymbolRef(null, name, RefKind.Inherit, line, fromPath));
            }
        }

        return rows;
    }

    // 'Base' binds by name, 'mixin.Mixin' by its last segment like any dotted callee, and 'Generic[T]' by
    // the type being parameterised rather than by the brackets
    private static string? PythonTargetName(Node node) => node.Type switch
    {
        "identifier" => node.Text,
        "attribute" => node.GetChildForField("attribute")?.Text,
        "subscript" => node.GetChildForField("value") is { } inner ? PythonTargetName(inner) : null,
        _ => null,
    };

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
            // python's dotted callee, and the same judgement as the two arms above: the member is the
            // name. The node kind is shared with rust's attribute (the #[...] shape), which can never
            // appear as a call's 'function' field — and rust's call gate is call_expression, so this arm
            // is only reached through a python call
            { Type: "attribute" } fn => fn.GetChildForField("attribute")?.Text,
            { Type: "field_expression" } fn => fn.GetChildForField("field")?.Text,
            // go's qualified callee: 'fmt.Println' is selector_expression(operand='fmt', field='Println'),
            // so the name is the member — go's own field spelling, which differs from javascript's
            // 'property' on member_expression even though all three grammars call the call node
            // call_expression. The receiver's identity is not part of the name, exactly as in the others
            { Type: "selector_expression" } fn => fn.GetChildForField("field")?.Text,
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

                // go's method owner hangs on the method as a field, not as an enclosing node, so the walk
                // above cannot see it: without this a call inside 'func (s *Store) Put' would enclose as
                // bare 'Put' while the symbol row it stands in reads ContainerName 'Store' + Name 'Put'
                if (current.Type == "method_declaration"
                    && TsSymbolExtractor.GoMethodOwnerName(current) is { } owner)
                {
                    names.Insert(0, owner);
                }
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
