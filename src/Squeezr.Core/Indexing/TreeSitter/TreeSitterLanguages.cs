using Squeezr.Core.Indexing;

namespace Squeezr.Core.Indexing.TreeSitter;

internal sealed record SymbolVocabulary(
    IReadOnlyDictionary<string, SymbolKind> Kinds,
    IReadOnlySet<string> ContainerTypes);

internal sealed record ReferenceVocabulary(
    string Pool,
    IReadOnlySet<string> CallTypes,
    IReadOnlySet<string> ImportTypes,
    IReadOnlySet<string> TypeTargetTypes);

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

    internal const string JsTsPool = "js-ts";
    internal const string RustPool = "rust";

    private sealed record LanguageSet(SymbolVocabulary Symbols, ReferenceVocabulary? References);

    // measured on the grammars bundled with TreeSitter.DotNet (see TreeSitterSpikeTests): no *_item
    // appears in a js/ts tree and no *_declaration/method_definition appears in a rust tree, so the
    // type-keyed rules in the extractors are only ever reached by the language that owns the type.
    // Proven for this pair, not for languages to come
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

    private static readonly SymbolVocabulary NoSymbols = new(
        new Dictionary<string, SymbolKind>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    private static readonly ReferenceVocabulary JsTsReferences = new(
        JsTsPool,
        new HashSet<string>(StringComparer.Ordinal) { "call_expression" },
        new HashSet<string>(StringComparer.Ordinal) { "import_statement", "export_statement" },
        new HashSet<string>(StringComparer.Ordinal));

    private static readonly ReferenceVocabulary RustReferences = new(
        RustPool,
        new HashSet<string>(StringComparer.Ordinal) { "call_expression", "macro_invocation" },
        new HashSet<string>(StringComparer.Ordinal) { "use_declaration" },
        new HashSet<string>(StringComparer.Ordinal) { "impl_item" });

    private static readonly Dictionary<string, LanguageSet> Sets = new(StringComparer.Ordinal)
    {
        [JavaScript] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [TypeScript] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [Tsx] = new LanguageSet(JsTsSymbols, JsTsReferences),
        [Rust] = new LanguageSet(RustSymbols, RustReferences),
    };

    internal static bool Routed(string language) =>
        Sets.ContainsKey(language);

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
