using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

public class TreeSitterRustSymbolTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string ItemsSource = """
        use std::collections::HashMap;

        pub struct Registry {
            entries: HashMap<String, String>,
        }

        pub enum Color {
            Red,
        }

        pub union Slot {
            raw: u32,
        }

        pub trait Lookup {
            fn find(&self, key: &str) -> Option<&String>;
        }

        impl Lookup for Registry {
            fn find(&self, key: &str) -> Option<&String> {
                self.entries.get(key)
            }
        }

        impl Registry {
            pub fn empty() -> Self {
                Registry { entries: HashMap::new() }
            }
        }

        mod utils {
            pub fn double(n: u32) -> u32 {
                let times_two = |x| x * 2;
                times_two(n)
            }
        }

        type Id = u64;

        const MAX: usize = 10;

        static BASE: usize = 42;

        macro_rules! shout {
            ($msg:expr) => {
                println!("{}", $msg);
            };
        }

        fn boot() -> Id {
            MAX as Id
        }
        """;

    [Fact]
    public void Rust_Items_ExtractedInSourceOrderWithNamesKindsAndContainers()
    {
        var result = Parse("src/lib.rs", ItemsSource);

        result.Language.ShouldBe("rust");
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Registry", SymbolKind.Class, null),
            ("Color", SymbolKind.Other, null),
            ("Slot", SymbolKind.Other, null),
            ("Lookup", SymbolKind.Interface, null),
            ("find", SymbolKind.Method, "Lookup"),
            ("Lookup for Registry", SymbolKind.Other, null),
            ("find", SymbolKind.Method, "Lookup for Registry"),
            ("Registry", SymbolKind.Other, null),
            ("empty", SymbolKind.Method, "Registry"),
            ("utils", SymbolKind.Other, null),
            ("double", SymbolKind.Function, "utils"),
            ("Id", SymbolKind.Other, null),
            ("MAX", SymbolKind.Field, null),
            ("BASE", SymbolKind.Field, null),
            ("shout", SymbolKind.Other, null),
            ("boot", SymbolKind.Function, null),
        ]);
    }

    [Fact]
    public void Rust_Items_SignaturesAreFirstLineWithoutOpeningBrace()
    {
        var symbols = Parse("src/lib.rs", ItemsSource).Symbols;

        Signature(symbols, "Registry", SymbolKind.Class).ShouldBe("pub struct Registry");
        Signature(symbols, "Color").ShouldBe("pub enum Color");
        Signature(symbols, "Slot").ShouldBe("pub union Slot");
        Signature(symbols, "Lookup").ShouldBe("pub trait Lookup");
        Signature(symbols, "find", SymbolKind.Method, "Lookup").ShouldBe(
            "fn find(&self, key: &str) -> Option<&String>;");
        Signature(symbols, "Lookup for Registry").ShouldBe("impl Lookup for Registry");
        Signature(symbols, "empty").ShouldBe("pub fn empty() -> Self");
        Signature(symbols, "utils").ShouldBe("mod utils");
        Signature(symbols, "Id").ShouldBe("type Id = u64;");
        Signature(symbols, "MAX").ShouldBe("const MAX: usize = 10;");
        Signature(symbols, "BASE").ShouldBe("static BASE: usize = 42;");
        Signature(symbols, "shout").ShouldBe("macro_rules! shout");
    }

    [Fact]
    public void Rust_Items_SpansCoverTheWholeItem()
    {
        var symbols = Parse("src/lib.rs", ItemsSource).Symbols;

        Find(symbols, "Registry", SymbolKind.Class).Span.ShouldBe(new CodeSpan(3, 5));
        Find(symbols, "find", SymbolKind.Method, "Lookup").Span.ShouldBe(new CodeSpan(16, 16));
        Find(symbols, "Lookup for Registry").Span.ShouldBe(new CodeSpan(19, 23));
        Find(symbols, "Registry", SymbolKind.Other).Span.ShouldBe(new CodeSpan(25, 29));
        Find(symbols, "double").Span.ShouldBe(new CodeSpan(32, 35));
        Find(symbols, "MAX").Span.ShouldBe(new CodeSpan(40, 40));
        Find(symbols, "shout").Span.ShouldBe(new CodeSpan(44, 48));
    }

    [Fact]
    public void Rust_ImplRows_AreTheContainerOfTheirOwnItems()
    {
        var symbols = Parse("src/lib.rs", ItemsSource).Symbols;

        // symbol_lookup pairs an owner with its members by container name, so the composed impl
        // name has to be byte-identical in both rows
        var traitImpl = Find(symbols, "Lookup for Registry");
        var inherentImpl = Find(symbols, "Registry", SymbolKind.Other);
        symbols.Where(s => s.ContainerName == traitImpl.Name)
            .Select(s => s.Name).ShouldBe(["find"]);
        symbols.Where(s => s.ContainerName == inherentImpl.Name)
            .Select(s => s.Name).ShouldBe(["empty"]);
        inherentImpl.Name.ShouldNotBe(traitImpl.Name);
    }

    [Fact]
    public void Rust_NestedFunction_ContainerChainIsOuterToInner()
    {
        var result = Parse("src/nested.rs", """
            impl Registry {
                pub fn outer() {
                    fn inner() {
                    }
                }
            }
            """);

        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Registry", SymbolKind.Other, null),
            ("outer", SymbolKind.Method, "Registry"),
            ("inner", SymbolKind.Function, "Registry.outer"),
        ]);
    }

    [Fact]
    public void Rust_FreeFunction_InsideModule_IsNotAMethod()
    {
        var symbols = Parse("src/free.rs", """
            mod api {
                pub fn serve() {
                }
            }
            """).Symbols;

        Find(symbols, "api").Kind.ShouldBe(SymbolKind.Other);
        Find(symbols, "serve").Kind.ShouldBe(SymbolKind.Function);
        Find(symbols, "serve").ContainerName.ShouldBe("api");
    }

    [Fact]
    public void Rust_Chunks_AreBuiltPerSymbolWithMatchingSpans()
    {
        var result = Parse("src/lib.rs", ItemsSource);

        result.Symbols.Count.ShouldBe(16);
        result.Chunks.Count.ShouldBe(16);
        foreach (var symbol in result.Symbols)
        {
            var chunk = result.Chunks.Single(c => c.Span == symbol.Span);
            chunk.Text.ShouldContain(symbol.Signature!);
            chunk.TokenCount.ShouldBeGreaterThan(0);
            chunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
        }
    }

    [Fact]
    public void Rust_NonItemNodes_ProduceNoSymbolRows()
    {
        var names = Parse("src/lib.rs", ItemsSource).Symbols.Select(s => s.Name).ToList();

        names.Count.ShouldBe(16);
        names.ShouldContain("Registry");
        names.ShouldNotContain("entries");
        names.ShouldNotContain("Red");
        names.ShouldNotContain("times_two");
        names.ShouldNotContain("println");
        names.ShouldNotContain("HashMap");
        names.ShouldNotContain("std");
    }

    [Fact]
    public void CrossGrammarSilence_HoldsForEveryLabelExceptTheFunctionDeclarationTwin()
    {
        using var jsGrammar = new Language("JavaScript");
        using var jsParser = new Parser(jsGrammar);
        using var jsTree = jsParser.Parse("function add(a, b) {\n  return a + b;\n}\n")!;

        // This pin was written as 'an unrouted label must not borrow the js/ts table', and the shape it
        // chose was right: a tree whose node names the js/ts table DOES match, so a fallback would show
        // up. What it asserted about go was true only because go had no table yet — a vacuous green of
        // exactly the kind T2/T3 kept naming. 'function_declaration' is the one kind name javascript
        // and tree-sitter-go share, so with the go table landed in T4 this call answers one row, and the
        // line says that out loud instead of pretending the silence was structural.
        // The exposure (a file whose label disagrees with the grammar that parsed it gets partial rows)
        // is reported as an obstacle; what production guarantees is that the pair cannot be assembled —
        // pinned by Provider_CouplesEveryRoutedExtensionToOneGrammarAndOneTable
        TsSymbolExtractor.Extract(jsTree.RootNode!, "javascript").Count.ShouldBe(1);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "python").ShouldBe([]);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "go").Select(s => (s.Name, s.Kind)).ShouldBe(
            [("add", SymbolKind.Function)]);

        using var rustGrammar = new Language("Rust");
        using var rustParser = new Parser(rustGrammar);
        using var rustTree = rustParser.Parse(ItemsSource)!;

        // rust shares only its root kind with go, so every foreign label stays silent on a rust tree
        TsSymbolExtractor.Extract(rustTree.RootNode!, "rust").Count.ShouldBe(16);
        TsSymbolExtractor.Extract(rustTree.RootNode!, "go").ShouldBe([]);
        TsSymbolExtractor.Extract(rustTree.RootNode!, "python").ShouldBe([]);
    }

    [Fact]
    public void Rust_FreeFunctionCall_ProducesCallReferenceInsideItsCaller()
    {
        var result = Parse("src/calls.rs", """
            fn helper(n: u32) -> u32 {
                n
            }

            fn boot() -> u32 {
                helper(1)
            }
            """);

        result.Symbols.Select(s => s.Name).ShouldBe(["helper", "boot"]);
        var call = result.Refs.ShouldHaveSingleItem();
        call.ToName.ShouldBe("helper");
        call.Kind.ShouldBe(RefKind.Call);
        call.Line.ShouldBe(6);
        call.EnclosingName.ShouldBe("boot");
    }

    [Fact]
    public void Rust_DocComments_AreNotCapturedYet()
    {
        var symbols = Parse("src/doc.rs", """
            /// Registry of entries.
            pub struct Registry {
                entries: u32,
            }
            """).Symbols;

        Find(symbols, "Registry").DocComment.ShouldBeNull();
    }

    [Fact]
    public void Rust_BrokenFile_PartialSymbolsWithoutException()
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(
            new FileRef("demo", "broken.rs"),
            Encoding.UTF8.GetBytes("""
            pub struct Registry {
                entries: u32,
            }

            pub fn broken(a: u32
                a
            }

            pub const MAX: usize = 10;

            pub fn recovered() -> u32 {
                1
            }
            """));

        provider.Warnings.ShouldBeEmpty();
        var names = result.Symbols.Select(s => s.Name).ToList();
        names.ShouldContain("Registry");
        names.ShouldContain("recovered");
        names.ShouldNotContain("broken");
    }

    private static ParseResult Parse(string path, string text) =>
        new TreeSitterProvider(Config).Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(text));

    private static Symbol Find(IReadOnlyList<Symbol> symbols, string name, SymbolKind? kind = null,
        string? container = null) =>
        symbols.Single(s => s.Name == name
                            && (kind is null || s.Kind == kind)
                            && (container is null || s.ContainerName == container));

    private static string Signature(IReadOnlyList<Symbol> symbols, string name,
        SymbolKind? kind = null, string? container = null) =>
        Find(symbols, name, kind, container).Signature!;
}
