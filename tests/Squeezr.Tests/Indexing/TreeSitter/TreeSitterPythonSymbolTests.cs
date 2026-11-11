using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Fact tests for the python symbol table, the mirror of TreeSitterRustSymbolTests: every expectation
// below was read off the grammar (python shape probe) before the test was written, not derived by hand
public class TreeSitterPythonSymbolTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string ItemsSource = """
        # module comment

        '''
        Module docstring.
        '''

        import os
        from typing import Iterable

        CONST = 3
        lower = 4
        MAX_SIZE = 8

        @dataclass
        class Box:
            '''Class docstring.'''

            name: str
            MAX_ITEMS: int = 5

            def label(self) -> str:
                self.cache = 1
                return self.name

        @staticmethod
        def decorated(b):
            def inner(c):
                return c
            return inner(b)

        async def fetch(url):
            return url

        def with_lambda(items):
            double = lambda x: x * 2
            return [double(i) for i in items]

        class Plain:
            # above amethod
            async def amethod(self):
                RESULT = 1
                return RESULT

        CFG.MODE = 1
        ITEMS[0] = 1
        """;

    [Fact]
    public void Python_Items_ExtractedInSourceOrderWithNamesKindsAndContainers()
    {
        var result = Parse("src/loader.py", ItemsSource);

        result.Language.ShouldBe("python");
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("CONST", SymbolKind.Field, null),
            ("MAX_SIZE", SymbolKind.Field, null),
            ("Box", SymbolKind.Class, null),
            ("MAX_ITEMS", SymbolKind.Field, "Box"),
            ("label", SymbolKind.Method, "Box"),
            ("decorated", SymbolKind.Function, null),
            ("inner", SymbolKind.Function, "decorated"),
            ("fetch", SymbolKind.Function, null),
            ("with_lambda", SymbolKind.Function, null),
            ("Plain", SymbolKind.Class, null),
            ("amethod", SymbolKind.Method, "Plain"),
        ]);
    }

    [Fact]
    public void Python_Items_SignaturesAreTheDefinitionLineWithoutBody()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        // python has no opening brace, so nothing is trimmed off the end and the def line keeps its
        // colon; a decorated item signs from its def, never from the decorator above it
        Signature(symbols, "CONST").ShouldBe("CONST = 3");
        Signature(symbols, "MAX_SIZE").ShouldBe("MAX_SIZE = 8");
        Signature(symbols, "Box", SymbolKind.Class).ShouldBe("class Box:");
        Signature(symbols, "MAX_ITEMS").ShouldBe("MAX_ITEMS: int = 5");
        Signature(symbols, "label", SymbolKind.Method, "Box").ShouldBe("def label(self) -> str:");
        Signature(symbols, "decorated").ShouldBe("def decorated(b):");
        Signature(symbols, "inner").ShouldBe("def inner(c):");
        Signature(symbols, "with_lambda").ShouldBe("def with_lambda(items):");
        Signature(symbols, "Plain", SymbolKind.Class).ShouldBe("class Plain:");
    }

    [Fact]
    public void Python_DecoratedItem_SpanStartsAtTheDecoratorWhileTheSignatureStaysOnTheDefinition()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        // the choice, with the evidence for it: '@dataclass' sits on row 14 and the class_definition
        // node itself on rows 15-23, yet the row spans 14-23. A routed file is taken off the fallback
        // chunker, so a decorator left outside every symbol span would drop out of the index — while
        // naming or signing from the wrapper would lose the definition (decorated_definition has no
        // name field at all), which is why the wrapper is only the span anchor
        Find(symbols, "Box", SymbolKind.Class).Span.ShouldBe(new CodeSpan(14, 23));
        Find(symbols, "Box", SymbolKind.Class).Signature.ShouldBe("class Box:");
        Find(symbols, "decorated").Span.ShouldBe(new CodeSpan(25, 29));
        Find(symbols, "decorated").Signature.ShouldBe("def decorated(b):");

        // one row per decorated item, not two: the wrapper is not keyed in the table
        symbols.Count(s => s.Name == "Box").ShouldBe(1);
        symbols.Count(s => s.Name == "decorated").ShouldBe(1);
    }

    [Fact]
    public void Python_UndecoratedItems_SpanTheirOwnNodeOnly()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        // the negative control for the rule above: nothing grows for an item with no wrapper, and the
        // span of a nested def is its own body, not the enclosing item
        Find(symbols, "label", SymbolKind.Method, "Box").Span.ShouldBe(new CodeSpan(21, 23));
        Find(symbols, "inner").Span.ShouldBe(new CodeSpan(27, 28));
        Find(symbols, "fetch").Span.ShouldBe(new CodeSpan(31, 32));
        Find(symbols, "Plain", SymbolKind.Class).Span.ShouldBe(new CodeSpan(38, 42));
        Find(symbols, "amethod", SymbolKind.Method, "Plain").Span.ShouldBe(new CodeSpan(40, 42));
        Find(symbols, "CONST").Span.ShouldBe(new CodeSpan(10, 10));
    }

    [Fact]
    public void Python_AsyncFunctions_AreOneFunctionRowCarryingTheKeywordInTheSignature()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        // tree-sitter-python has no async_function_definition: 'async' is a keyword inside the same
        // function_definition node, so the async pair needs no extra table row — and it must not
        // produce a second one either
        Find(symbols, "fetch").Kind.ShouldBe(SymbolKind.Function);
        Find(symbols, "fetch").Signature.ShouldBe("async def fetch(url):");
        symbols.Count(s => s.Name == "fetch").ShouldBe(1);
        Find(symbols, "amethod", SymbolKind.Method, "Plain").Signature.ShouldBe("async def amethod(self):");
    }

    [Fact]
    public void Python_ClassBody_IsTheContainerOfItsMethodsAndConstants()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        // a def inside a class is python's method shape — the grammar has no method node, so the kind
        // comes from the enclosing definition the same way rust reads an impl block
        symbols.Where(s => s.ContainerName == "Box")
            .Select(s => (s.Name, s.Kind)).ShouldBe([("MAX_ITEMS", SymbolKind.Field), ("label", SymbolKind.Method)]);
        symbols.Where(s => s.ContainerName == "Plain")
            .Select(s => (s.Name, s.Kind)).ShouldBe([("amethod", SymbolKind.Method)]);
    }

    [Fact]
    public void Python_NestedFunction_ContainerChainIsOuterToInner()
    {
        var result = Parse("src/nested.py", """
            class Outer:
                @staticmethod
                def mid():
                    def inner():
                        pass
            """);

        // two facts in one chain: the wrapper is not a container, so 'inner' lands under 'Outer.mid'
        // and not under a name invented for the decorator; and the kind rule reads the enclosing
        // definition, so a def under a class is a Method even when a decorator makes it a static one —
        // tree-sitter-python has no method node and no staticmethod node, only the decorator text, which
        // is a reference question for T3 rather than a kind question here
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Outer", SymbolKind.Class, null),
            ("mid", SymbolKind.Method, "Outer"),
            ("inner", SymbolKind.Function, "Outer.mid"),
        ]);
    }

    [Fact]
    public void Python_ModuleConstants_AreFieldRows_WhileWorkingAssignmentsAreNotRows()
    {
        var symbols = Parse("src/loader.py", ItemsSource).Symbols;

        var names = symbols.Select(s => s.Name).ToList();
        names.Count.ShouldBe(11);

        // the gate has to be read as two conditions, and each arm needs its control in the same file:
        // CONST/MAX_SIZE are module-level and UPPER_SNAKE, MAX_ITEMS is class-level and UPPER_SNAKE
        names.ShouldContain("CONST");
        names.ShouldContain("MAX_SIZE");
        names.ShouldContain("MAX_ITEMS");

        // lowercase module binding, the bare annotation, a lambda bound inside a def body, an uppercase
        // write inside a def body and two module-level writes to an existing attribute or element — all
        // the same 'assignment' node kind, none of them declarations
        names.ShouldNotContain("lower");
        names.ShouldNotContain("name");
        names.ShouldNotContain("cache");
        names.ShouldNotContain("double");

        // RESULT is UPPER_SNAKE exactly like the rows above, and is still not a declaration because the
        // statement sits in a def body — this is the arm the scope walk carries, nothing else kills it
        names.ShouldNotContain("RESULT");
        names.ShouldNotContain("CFG.MODE");
        names.ShouldNotContain("ITEMS[0]");
    }

    [Fact]
    public void Python_Chunks_AreBuiltPerSymbolWithMatchingSpans()
    {
        var result = Parse("src/loader.py", ItemsSource);

        result.Symbols.Count.ShouldBe(11);
        result.Chunks.Count.ShouldBe(11);
        foreach (var symbol in result.Symbols)
        {
            var chunk = result.Chunks.Single(c => c.Span == symbol.Span);
            chunk.Text.ShouldContain(symbol.Signature!);
            chunk.TokenCount.ShouldBeGreaterThan(0);
            chunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
        }
    }

    [Fact]
    public void Python_NonItemNodes_ProduceNoSymbolRows()
    {
        var names = Parse("src/loader.py", ItemsSource).Symbols.Select(s => s.Name).ToList();

        names.Count.ShouldBe(11);
        names.ShouldContain("Box");

        // imports, the docstring text, decorator names, parameters, comprehension variables and the
        // identifiers inside bodies: none of them a keyed node
        names.ShouldNotContain("os");
        names.ShouldNotContain("Iterable");
        names.ShouldNotContain("dataclass");
        names.ShouldNotContain("staticmethod");
        names.ShouldNotContain("Module docstring.");
        names.ShouldNotContain("self");
        names.ShouldNotContain("items");
        names.ShouldNotContain("url");
        names.ShouldNotContain("x");
        names.ShouldNotContain("i");
    }

    [Fact]
    public void Python_DocstringsAndComments_AreNotCapturedYet()
    {
        var symbols = Parse("src/doc.py", """
            '''Class docstring for Note.'''

            class Note:
                # above the method
                def speak(self):
                    return 1
            """).Symbols;

        // the denominator makes this a fence rather than a silence: two rows were extracted and both
        // have no doc text. A python docstring is a 'string' node, so CollectComments never sees it,
        // and the '# comment' node it does see is not '/**' — captured documentation is a separate
        // decision, not something a later task should rediscover from a green test
        symbols.Count.ShouldBe(2);
        Find(symbols, "Note", SymbolKind.Class).DocComment.ShouldBeNull();
        Find(symbols, "speak", SymbolKind.Method, "Note").DocComment.ShouldBeNull();
    }

    [Fact]
    public void Python_BrokenFile_PartialSymbolsWithoutException()
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(
            new FileRef("demo", "broken.py"),
            Encoding.UTF8.GetBytes("""
                MAX_SIZE = 8

                def intact():
                    return compute(1

                class Later:
                    def method(self):
                        return 1
                """));

        // measured recovery shape, deliberately weaker than the rust pin: an unterminated '(' swallows
        // everything after it, so 'intact' comes back with a span that ran to the end of the file and
        // 'Later' never appears as a node at all. Rows before the break survive, rows after it do not,
        // the file is still indexed, and no warning is raised for a tree that merely has an ERROR node
        provider.Warnings.ShouldBeEmpty();
        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe(
        [
            ("MAX_SIZE", SymbolKind.Field),
            ("intact", SymbolKind.Function),
        ]);
        Find(result.Symbols, "intact").Span.ShouldBe(new CodeSpan(3, 8));
    }

    [Fact]
    public void PythonTable_ExtractsNothingFromJsOrGoTrees_WhileTheirOwnLabelsHarvestThem()
    {
        using var jsGrammar = new Language("JavaScript");
        using var jsParser = new Parser(jsGrammar);
        using var jsTree = jsParser.Parse("""
            const ITEMS = [1, 2];

            export function run(items) {
              let total = 0;
              for (const item of items) {
                total = mul(total, item);
                ITEMS[0] = total;
              }
              return total;
            }

            export class Holder {
              clear() {
                this.items = [];
              }
            }
            """)!;

        // the ac5 arms, each with the control that proves the tree is harvestable at all: javascript
        // writes assignment_expression and go writes assignment_statement for the shape python calls
        // 'assignment', so the python table finds no name it keys on — and 'var Counter = 0', which a
        // case-insensitive shared pool would happily read as a python constant, is a var_declaration
        TsSymbolExtractor.Extract(jsTree.RootNode!, "javascript").Count.ShouldBe(3);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "python").ShouldBe([]);

        using var goGrammar = new Language("Go");
        using var goParser = new Parser(goGrammar);
        using var goTree = goParser.Parse("""
            package registry

            var Counter = 0

            func Run(items []string) string {
                total := 0
                for _, item := range items {
                    total = total + len(item)
                }
                return ""
            }
            """)!;

        TsSymbolExtractor.Extract(goTree.RootNode!, "javascript").Count.ShouldBe(1);
        TsSymbolExtractor.Extract(goTree.RootNode!, "python").ShouldBe([]);

        // and the wall holds in the other direction: the same python tree that yields eleven rows under
        // its own label yields none through the js/ts table
        using var pyGrammar = new Language("Python");
        using var pyParser = new Parser(pyGrammar);
        using var pyTree = pyParser.Parse(ItemsSource)!;

        TsSymbolExtractor.Extract(pyTree.RootNode!, "python").Count.ShouldBe(11);
        TsSymbolExtractor.Extract(pyTree.RootNode!, "javascript").ShouldBe([]);
        TsSymbolExtractor.Extract(pyTree.RootNode!, "go").ShouldBe([]);
    }

    // separate fixture on purpose: these are same-span pairs, and folding them into ItemsSource would
    // move every measured row number in this file for no evidence gain
    private const string SameSpanSource = """
        class Config: DEBUG = True

        X = Y = 1
        """;

    [Fact]
    public void Python_SameSpanSymbols_EmitOneChunkPerSymbolSoThePairDuplicates()
    {
        var result = Parse("src/config.py", SameSpanSource);

        // measured shape, pinned as declared behavior (tracked in BUGS/022, not fixed here): a one-line
        // class and the const inside it share a span because a span is the node's line range, so the
        // per-symbol chunker emits two byte-identical rows for one line. The style the rest of this file
        // uses — Chunks.Single(c => c.Span == symbol.Span) — cannot resolve a chunk here, which is why
        // this pin counts instead: the collision is the finding, not something to look past
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Config", SymbolKind.Class, null),
            ("DEBUG", SymbolKind.Field, "Config"),
            ("X", SymbolKind.Field, null),
            ("Y", SymbolKind.Field, null),
        ]);
        result.Symbols.Select(s => s.Span).Distinct().Count().ShouldBe(2);
        result.Chunks.Count.ShouldBe(4);
        result.Chunks.Select(c => c.Span).Distinct().Count().ShouldBe(2);

        var config = Find(result.Symbols, "Config", SymbolKind.Class);
        var duplicate = result.Chunks.Where(c => c.Span == config.Span).ToList();
        duplicate.Count.ShouldBe(2);
        duplicate.ShouldAllBe(c => c.Text == "class Config: DEBUG = True");
        duplicate.ShouldAllBe(c => c.ChunkHash == duplicate[0].ChunkHash);

        // the chained form is the second trigger: 'X = Y = 1' is an assignment whose value is another
        // assignment, so both targets become rows on the same line and their chunks are identical to the
        // pair above in everything the chunker records — only the signatures tell the symbols apart
        var chained = result.Chunks.Where(c => c.Span.StartLine == 3).ToList();
        chained.Count.ShouldBe(2);
        chained.ShouldAllBe(c => c.Text == "X = Y = 1");
        result.Symbols.Select(s => s.Signature).ShouldBe(
        ["class Config: DEBUG = True", "DEBUG = True", "X = Y = 1", "Y = 1"]);
    }

    [Fact]
    public void Python_TupleTargets_AreNotRows_WhileABareAnnotationIs()
    {
        var symbols = Parse("src/tuple.py", """
            A, B = 1, 2
            MAX: int
            COUNT = 1
            """).Symbols;

        // the tuple form is a known drop, not an oversight: the target's text carries the comma, so the
        // UPPER_SNAKE test rejects 'A, B' and neither name becomes a row. A bare uppercase annotation and
        // a plain constant do arrive — the control that the walker reached this file at all
        symbols.Select(s => s.Name).ShouldBe(["MAX", "COUNT"]);
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
