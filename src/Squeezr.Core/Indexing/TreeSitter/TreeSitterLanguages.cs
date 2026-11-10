using Squeezr.Core.Indexing;

namespace Squeezr.Core.Indexing.TreeSitter;

internal sealed record SymbolVocabulary(
    IReadOnlyDictionary<string, SymbolKind> Kinds,
    IReadOnlySet<string> ContainerTypes,
    string? DecoratorWrapper = null);

internal sealed record ReferenceVocabulary(
    string Pool,
    IReadOnlySet<string> CallTypes,
    IReadOnlySet<string> ImportTypes,
    IReadOnlySet<string> TypeTargetTypes,
    ReferenceReader Reader);

// Which field names the import and class-target readers speak. Node-kind names are NOT a safe dispatch
// key across grammars — python writes an import node called 'import_statement', the same name
// javascript uses, and the shapes under it share nothing (js: 'source' + 'import_clause'; python:
// 'module_name' + dotted_name children). Keying the emitter on the node kind would run the javascript
// reader over a python import, find no source field and emit zero references — silently. The kinds the
// walkers gate on (call / class_definition) are disjoint, measured per grammar, so only the field
// readers need this tag. No default value on purpose: a new language row has to say which reader it
// speaks instead of inheriting javascript's.
internal enum ReferenceReader
{
    JsTs,
    Rust,
    Python,
}

// The single home of tree-sitter language knowledge: which languages are routed, which node types
// each grammar contributes to symbols and to references, and which files may match each other's
// names at all. Adding a language here is a data addition; the provider, the indexer gate, the
// phase predicate and the reference pool all read this file, so no language list lives anywhere else.
//
// Pools are the cross-language firewall: a reference edge is name-matched only against symbols of
// files in the same pool, because 'mul' in javascript and 'mul' in rust are two unrelated things.
// javascript, typescript and tsx share one pool exactly as they did before rust arrived — that is
// what keeps js/ts reference output byte-stable while a new language joins routed.
internal static class TreeSitterLanguages
{
    internal const string JavaScript = "javascript";
    internal const string TypeScript = "typescript";
    internal const string Tsx = "tsx";
    internal const string Rust = "rust";
    internal const string Python = "python";
    internal const string Go = "go";

    internal const string JsTsPool = "js-ts";
    internal const string RustPool = "rust";

    // reserved now so a grammar's pool name is not invented mid-extraction later: python and go are
    // registered below without reference rules, and these are the names their rows will carry
    internal const string PythonPool = "python";
    internal const string GoPool = "go";

    private sealed record LanguageSet(SymbolVocabulary Symbols, ReferenceVocabulary? References);

    // measured on the grammars bundled with TreeSitter.DotNet (see TreeSitterSpikeTests): no *_item
    // appears in a js/ts tree and no *_declaration/method_definition appears in a rust tree, so the
    // type-keyed rules in the extractors are only ever reached by the language that owns the type.
    // python extends the proof, not the assumption: function_definition / class_definition /
    // decorated_definition / assignment appear in neither a js nor a go tree, where the near-miss
    // names are assignment_expression (js) and assignment_statement + short_var_declaration (go)
    // — so a python row keyed on 'assignment' cannot harvest another grammar's statements
    private static readonly SymbolVocabulary JsTsSymbols = new(
        new Dictionary<string, SymbolKind>(StringComparer.Ordinal)
        {
            ["function_declaration"] = SymbolKind.Function,
            ["function_expression"] = SymbolKind.Function,
            ["method_definition"] = SymbolKind.Method,
            ["class_declaration"] = SymbolKind.Class,
            ["interface_declaration"] = SymbolKind.Interface,
            ["type_alias_declaration"] = SymbolKind.Other,
            ["variable_declarator"] = SymbolKind.Function,
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "class_declaration",
            "interface_declaration",
            "function_declaration",
            "method_definition",
            "type_alias_declaration",
            "function_expression",
            "variable_declarator",
        });

    private static readonly SymbolVocabulary RustSymbols = new(
        new Dictionary<string, SymbolKind>(StringComparer.Ordinal)
        {
            ["function_item"] = SymbolKind.Function,
            ["function_signature_item"] = SymbolKind.Function,
            ["struct_item"] = SymbolKind.Class,
            ["trait_item"] = SymbolKind.Interface,
            ["impl_item"] = SymbolKind.Other,
            ["enum_item"] = SymbolKind.Other,
            ["union_item"] = SymbolKind.Other,
            ["mod_item"] = SymbolKind.Other,
            ["type_item"] = SymbolKind.Other,
            ["const_item"] = SymbolKind.Field,
            ["static_item"] = SymbolKind.Field,
            ["macro_definition"] = SymbolKind.Other,
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "mod_item",
            "impl_item",
            "trait_item",
            "function_item",
        });

    // measured on tree-sitter-python (TreeSitterSpikeTests.Python_*): 'async def' is still a
    // function_definition with 'async' as a keyword inside it, and a decorator WRAPS the definition in
    // a decorated_definition that carries no name field of its own — so the two definition kinds are
    // keyed and the wrapper is only the span anchor. Keying the wrapper as a row of its own was tried
    // and is inert rather than a double count: with no name field and no identifier child, NameOf
    // answers null and no row is produced at all, which is the worse failure (a dead table entry that
    // looks covered) — only the table-size pin in the registration tests would ever notice it.
    // 'assignment' is python's binding statement and the closest thing to rust's const_item, but unlike
    // rust it is also the shape of every write to a local, so the extractor keeps only a const-shaped
    // target outside a function body
    private static readonly SymbolVocabulary PythonSymbols = new(
        new Dictionary<string, SymbolKind>(StringComparer.Ordinal)
        {
            ["function_definition"] = SymbolKind.Function,
            ["class_definition"] = SymbolKind.Class,
            ["assignment"] = SymbolKind.Field,
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "class_definition",
            "function_definition",
        },
        DecoratorWrapper: "decorated_definition");

    private static readonly SymbolVocabulary NoSymbols = new(
        new Dictionary<string, SymbolKind>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    private static readonly ReferenceVocabulary JsTsReferences = new(
        JsTsPool,
        new HashSet<string>(StringComparer.Ordinal) { "call_expression" },
        new HashSet<string>(StringComparer.Ordinal) { "import_statement", "export_statement" },
        new HashSet<string>(StringComparer.Ordinal),
        ReferenceReader.JsTs);

    private static readonly ReferenceVocabulary RustReferences = new(
        RustPool,
        new HashSet<string>(StringComparer.Ordinal) { "call_expression", "macro_invocation" },
        new HashSet<string>(StringComparer.Ordinal) { "use_declaration" },
        new HashSet<string>(StringComparer.Ordinal) { "impl_item" },
        ReferenceReader.Rust);

    // measured shapes (TreeSitterSpikeTests.Python_CallCallee_…, Python_ImportStatement_…,
    // Python_ImportFromStatement_…, Python_ClassSuperclasses_…): a python call is 'call' with the callee in
    // field 'function' (identifier, attribute chain, or a subscript nobody can name), an import is a
    // statement whose bindings are dotted_name / aliased_import CHILDREN (the name field repeats, so a
    // field read loses every name after the first), and a class target list is field 'superclasses' — an
    // argument_list that mixes the bases with 'metaclass=ABCMeta' keyword arguments
    private static readonly ReferenceVocabulary PythonReferences = new(
        PythonPool,
        new HashSet<string>(StringComparer.Ordinal) { "call" },
        new HashSet<string>(StringComparer.Ordinal) { "import_statement", "import_from_statement" },
        new HashSet<string>(StringComparer.Ordinal) { "class_definition" },
        ReferenceReader.Python);

    private static readonly Dictionary<string, LanguageSet> Sets = new(StringComparer.Ordinal)
    {
        [JavaScript] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [TypeScript] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [Tsx] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [Rust] = new LanguageSet(RustSymbols, RustReferences),

        // python's symbol table routes the language and its reference table gives it its own pool, so a
        // python file can only ever match a python symbol; the pool name was reserved in T1 for exactly
        // this row
        [Python] = new LanguageSet(PythonSymbols, PythonReferences),

        // registered ahead of its vocabulary so the grammar arms, the pool name and this row are
        // already in place when a symbol table lands; empty Kinds is what keeps it unrouted
        [Go] = new LanguageSet(NoSymbols, null),
    };

    // routing is 'has a symbol vocabulary', not 'has a row here': a routed file is taken off the
    // fallback chunker, so routing a language whose vocabulary is EMPTY would index it to zero symbols
    // AND zero chunks (the trap 111/T1 nearly shipped). Deriving the predicate makes exactly that
    // state impossible to write — it says nothing about a vocabulary that is non-empty but wrong,
    // which is the per-language extraction tests' job, nor about a half-landed table: the guard that a
    // routed file never loses its chunks to a missing entry is the fallback tripwire in
    // TreeSitterRoutingTests, which T2/T4 convert to per-symbol assertions rather than delete
    internal static bool Routed(string language) =>
        Sets.TryGetValue(language, out var set) && set.Symbols.Kinds.Count > 0;

    // an unrouted language yields zero rows rather than borrowing the js/ts table: tree-sitter-go
    // names a plain function node function_declaration too, so a js/ts fallback would silently
    // extract it through the wrong rules. Silence here is loud downstream — routing and
    // chunk-coverage assertions already fail on a file that produced no symbols
    internal static SymbolVocabulary SymbolsFor(string language) =>
        Sets.TryGetValue(language, out var set) ? set.Symbols : NoSymbols;

    internal static ReferenceVocabulary? ReferencesFor(string language) =>
        Sets.TryGetValue(language, out var set) ? set.References : null;

    // the pool question, asked once: the phase needs to know which files may match each other's
    // names, not how a reference vocabulary is shaped
    internal static string? ReferencePoolFor(string language) => ReferencesFor(language)?.Pool;
}
