using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Fact tests for the go symbol table, the mirror of TreeSitterRustSymbolTests and
// TreeSitterPythonSymbolTests: every expectation was read off the grammar through a throwaway probe
// before the test was written, including the shapes this grammar spells differently from the two
// languages that landed before it (a type is named inside its declaration wrapper, a method's owner is a
// field rather than an ancestor, and a package-level const is the same node kind as a local one).
public class TreeSitterGoSymbolTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string ItemsSource = """
        package registry

        import "fmt"

        // Run starts the service.
        func Run(items []string) string {
            return fmt.Sprint(items)
        }

        type Registry struct {
            items []string
            count int
        }

        type Lookuper interface {
            Lookup(key string) string
            All() []string
        }

        type Mode string

        const MaxSize = 128

        const (
            A int = 1
            B = 2
        )

        var Shared = "x"

        var (
            Counter int
            Limit   = 10
        )

        func (r *Registry) Add(item string) *Registry {
            const local = 1
            var scratch int
            r.items = append(r.items, item)
            fmt.Println(scratch, local)
            return r
        }

        func (r Registry) Len() int {
            return len(r.items)
        }

        func Map[K comparable, V any](m map[K]V) int {
            return len(m)
        }

        func (r *Generic[T]) Get(key string) T {
            var zero T
            return zero
        }

        type Generic[T any] struct {
            value T
        }

        func anonymous() {
            handler := func(x int) int { return x * 2 }
            _ = handler
        }
        """;

    private const string JsSource = """
        import { mul } from './util';

        export function run(items) {
          let total = 0;
          for (const item of items) {
            total = mul(total, item);
          }
          return total;
        }

        export class Holder {
          clear() {
            this.items = [];
          }
        }
        """;

    [Fact]
    public void Go_Items_ExtractedInSourceOrderWithNamesKindsAndContainers()
    {
        var result = Parse("src/registry.go", ItemsSource);

        result.Language.ShouldBe("go");
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Run", SymbolKind.Function, null),
            ("Registry", SymbolKind.Class, null),
            ("Lookuper", SymbolKind.Interface, null),
            ("Lookup", SymbolKind.Method, "Lookuper"),
            ("All", SymbolKind.Method, "Lookuper"),
            ("Mode", SymbolKind.Other, null),
            ("MaxSize", SymbolKind.Field, null),
            ("A", SymbolKind.Field, null),
            ("B", SymbolKind.Field, null),
            ("Shared", SymbolKind.Field, null),
            ("Counter", SymbolKind.Field, null),
            ("Limit", SymbolKind.Field, null),
            ("Add", SymbolKind.Method, "Registry"),
            ("Len", SymbolKind.Method, "Registry"),
            ("Map", SymbolKind.Function, null),
            ("Get", SymbolKind.Method, "Generic"),
            ("Generic", SymbolKind.Class, null),
            ("anonymous", SymbolKind.Function, null),
        ]);
    }

    [Fact]
    public void Go_Items_SignaturesCarryTheDeclarationKeywordForASingleBindingOnly()
    {
        var symbols = Parse("src/registry.go", ItemsSource).Symbols;

        // a spec node starts after its keyword ('Registry struct {'), so a single declaration is hoisted
        // to the line an agent would actually search for — the same answer rust gives for const_item.
        // A GROUP opens with 'const (' / 'var (' on a line of its own, and there the spec's own line is
        // the only one that says anything about that member, so it is left alone
        Signature(symbols, "Run", SymbolKind.Function).ShouldBe("func Run(items []string) string");
        Signature(symbols, "Registry", SymbolKind.Class).ShouldBe("type Registry struct");
        Signature(symbols, "Lookuper", SymbolKind.Interface).ShouldBe("type Lookuper interface");
        Signature(symbols, "Mode", SymbolKind.Other).ShouldBe("type Mode string");
        Signature(symbols, "MaxSize").ShouldBe("const MaxSize = 128");
        Signature(symbols, "Shared").ShouldBe("var Shared = \"x\"");
        Signature(symbols, "A").ShouldBe("A int = 1");
        Signature(symbols, "B").ShouldBe("B = 2");
        Signature(symbols, "Counter").ShouldBe("Counter int");
        // the spacing is authored text, not a typo to normalise: 'Limit   = 10' keeps the gap the spec
        // node's own line has, and the same holds for a receiver form read from its own line
        Signature(symbols, "Limit").ShouldBe("Limit   = 10");
        Signature(symbols, "Add", SymbolKind.Method, "Registry")
            .ShouldBe("func (r *Registry) Add(item string) *Registry");
        Signature(symbols, "Map").ShouldBe("func Map[K comparable, V any](m map[K]V) int");
        Signature(symbols, "Get", SymbolKind.Method, "Generic")
            .ShouldBe("func (r *Generic[T]) Get(key string) T");
    }

    [Fact]
    public void Go_Items_SpansCoverTheWholeItem_AndNoTwoRowsShareASpan()
    {
        var symbols = Parse("src/registry.go", ItemsSource).Symbols;

        Find(symbols, "Run", SymbolKind.Function).Span.ShouldBe(new CodeSpan(6, 8));
        Find(symbols, "Registry", SymbolKind.Class).Span.ShouldBe(new CodeSpan(10, 13));
        Find(symbols, "Lookuper", SymbolKind.Interface).Span.ShouldBe(new CodeSpan(15, 18));
        Find(symbols, "Add", SymbolKind.Method, "Registry").Span.ShouldBe(new CodeSpan(36, 42));
        Find(symbols, "Get", SymbolKind.Method, "Generic").Span.ShouldBe(new CodeSpan(52, 55));
        Find(symbols, "MaxSize").Span.ShouldBe(new CodeSpan(22, 22));

        // the group members keep their own one-line spans, which is why the wrapper is neither keyed nor
        // used as a span anchor: python's decorator wraps exactly one item, a go group wraps several
        Find(symbols, "A").Span.ShouldBe(new CodeSpan(25, 25));
        Find(symbols, "B").Span.ShouldBe(new CodeSpan(26, 26));
        Find(symbols, "Counter").Span.ShouldBe(new CodeSpan(32, 32));
        Find(symbols, "Limit").Span.ShouldBe(new CodeSpan(33, 33));
        symbols.Select(s => s.Span).Distinct().Count().ShouldBe(symbols.Count);
    }

    [Fact]
    public void Go_MethodContainers_AreTheReceiverTypeNamedExactlyLikeItsOwnRow()
    {
        var symbols = Parse("src/registry.go", ItemsSource).Symbols;

        // the pairing symbol_lookup depends on: a method's container string must equal the type row's
        // name, pointer receiver, value receiver or generic receiver alike. Rust reaches the same
        // invariant through an impl ancestor; go has no such node, so the receiver is unwrapped —
        // '*Registry' and 'Generic[T]' both end at the base name the type itself is called
        symbols.Where(s => s.ContainerName == "Registry").Select(s => s.Name).ShouldBe(["Add", "Len"]);
        symbols.Where(s => s.ContainerName == "Generic").Select(s => s.Name).ShouldBe(["Get"]);
        Find(symbols, "Registry", SymbolKind.Class).Name.ShouldBe("Registry");
        Find(symbols, "Generic", SymbolKind.Class).Name.ShouldBe("Generic");

        // a generic function is not a method: its type parameters are not a receiver, and 'Map' stays a
        // top-level Function with no container even though its name reads like a type
        Find(symbols, "Map", SymbolKind.Function).ContainerName.ShouldBeNull();
    }

    [Fact]
    public void Go_InterfaceMethods_AreRowsUnderTheInterfaceRow()
    {
        var symbols = Parse("src/registry.go", ItemsSource).Symbols;

        // method_elem is go's own exposure of an interface's method set — the shape rust spells with
        // function_signature_item inside a trait_item, and it reads the same way here: Method rows whose
        // container is the enclosing type_spec's name
        symbols.Where(s => s.ContainerName == "Lookuper")
            .Select(s => (s.Name, s.Kind)).ShouldBe([("Lookup", SymbolKind.Method), ("All", SymbolKind.Method)]);
        Find(symbols, "Lookup", SymbolKind.Method, "Lookuper").Span.ShouldBe(new CodeSpan(16, 16));
        Find(symbols, "All", SymbolKind.Method, "Lookuper").Signature.ShouldBe("All() []string");
    }

    [Fact]
    public void Go_BodyLevelBindings_AreNotRows_WhilePackageLevelOnesAre()
    {
        var names = Parse("src/registry.go", ItemsSource).Symbols.Select(s => s.Name).ToList();

        names.Count.ShouldBe(18);

        // 'const local = 1' and 'var scratch int' inside Add's body, and 'var zero T' inside Get's, are
        // const_spec / var_spec nodes exactly like the package-level ones; only the ancestor chain says
        // which is which. Dropping them is go's own decision, NOT a rule inherited from rust — rust's
        // const_item and static_item do sit inside blocks and rust ships rows for them (MEASURED in
        // review-023-05 and recorded in audits/review-023-05-mutation.txt: a 'const K' and a 'static S'
        // in an fn body arrive as Field rows with container 'main'. Not pinned here or in the rust tests:
        // no rust fixture has a block-level item yet, and adding one is a coverage task, not a comment
        // fix — the claim's home is the carrier until that row exists)
        names.ShouldNotContain("local");
        names.ShouldNotContain("scratch");
        names.ShouldNotContain("zero");

        // ':=' is another kind again (short_var_declaration) and has no row either
        names.ShouldNotContain("handler");
        names.ShouldNotContain("x");

        // the controls: the package-level bindings of the same three shapes DID arrive
        names.ShouldContain("MaxSize");
        names.ShouldContain("Counter");
        names.ShouldContain("Shared");
    }

    [Fact]
    public void Go_BlankIdentifiers_AreNotRows_AtAnyKind()
    {
        // measured before the refusal existed: each of the seven blank forms below produced a row named
        // '_'. The blank is a throwaway binding in go — 'var _ Sailer = (*Boat)(nil)' reserves a type
        // without naming anything, an interface element called '_' cannot be reached at all — so nothing
        // here is addressable, and every shipped language refuses the equivalent in its own way (js needs
        // an arrow, python needs letters, rust keys nothing that could be blank)
        var rows = Parse("src/blank.go", """
            package fleet

            const _ = iota

            var _ Sailer = (*Boat)(nil)

            type _ int

            type _ = int

            func _() {}

            type T struct{}

            func (t T) _() {}

            type Sink interface {
                _() error
            }

            type Reader interface {
                Do() error
            }
            """).Symbols;

        // the two forms the first version of this pin missed, and the reason they are here rather than
        // assumed: 'type _ = int' is a type_alias (a kind that only got its own row with review-023-05
        // B-3), and 'type Sink interface { _() error }' is a method_elem inside a NAMED interface — the
        // anonymous-interface refusal would have hidden that case behind a different rule. Both are
        // refused by the name test, not by scope or ownership
        rows.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("T", SymbolKind.Class, null),
            ("Sink", SymbolKind.Interface, null),
            ("Reader", SymbolKind.Interface, null),
            ("Do", SymbolKind.Method, "Reader"),
        ]);
        rows.ShouldNotContain(s => s.Name == "_");
    }

    [Fact]
    public void Go_MultiNameSpecs_AreRefused_ratherThanSharingOneSpan()
    {
        // 'const A, B = 1, 2' is ONE const_spec whose name field holds 'A' and whose second identifier is
        // an unnamed sibling. Reading one row per name would give A and B the same span, which is the
        // BUGS/022 duplicate-chunk shape that triage has not given a span model to — so the form is
        // refused whole, exactly as python refuses a tuple target, and the single-name specs beside it
        // are untouched
        var rows = Parse("src/multi.go", """
            package p

            const A, B = 1, 2

            var C, D = 3, 4

            const Solo = 5

            var Pair int
            """).Symbols;

        rows.Select(s => s.Name).ShouldBe(["Solo", "Pair"]);
        rows.ShouldNotContain(s => s.Name == "A");
        rows.ShouldNotContain(s => s.Name == "B");
    }

    [Fact]
    public void Go_TypeAliases_AreRows_BothAloneAndInsideAGroup()
    {
        // 'type Alias = int' is a type_alias node, not a type_spec: an earlier draft keyed only the
        // latter and so indexed no go alias at all, while javascript (type_alias_declaration) and rust
        // (type_item) both index theirs. Other is the kind the other two give an alias, and the group
        // member keeps the hoist rule's other branch — its own line, without the group's 'type (' head
        var rows = Parse("src/alias.go", """
            package p

            type Alias = int

            type (
                Alias2 = string
                Named int
            )
            """).Symbols;

        rows.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Alias", SymbolKind.Other, null),
            ("Alias2", SymbolKind.Other, null),
            ("Named", SymbolKind.Other, null),
        ]);
        Signature(rows, "Alias", SymbolKind.Other).ShouldBe("type Alias = int");
        Signature(rows, "Alias2", SymbolKind.Other).ShouldBe("Alias2 = string");
    }

    [Fact]
    public void Go_LocalTypeBindings_KeepTheirOwner_WhileAnAnonymousInterfaceHasNone()
    {
        // the split decided in review-023-05: a type declared inside a function is still named, so its
        // rows are addressable and attributed under the function; a method_elem inside an interface that
        // no type declaration names has no byte-stable container at all, so it is refused rather than
        // shipped as an ownerless Method row reachable by name but never by member lookup
        var locals = Parse("src/local.go", """
            package p

            func Make() int {
                type Local struct {
                    v int
                }

                type Reader interface {
                    Read(n int) int
                }

                _ = Local{}
                _ = Reader(nil)
                return 0
            }
            """).Symbols;

        locals.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Make", SymbolKind.Function, null),
            ("Local", SymbolKind.Class, "Make"),
            ("Reader", SymbolKind.Interface, "Make"),
            // a local interface keeps its element rows, attributed through the chain the same way a
            // python nested def is: the type's own container is the function, so the element's is both
            ("Read", SymbolKind.Method, "Make.Reader"),
        ]);

        var anonymous = Parse("src/anonymous.go", "package p\n\nvar Sink interface{ Do() error }\n").Symbols;

        // 'Sink' itself is a real package-level var and stays; only the ownerless element is refused, so
        // the empty-container row that used to appear is gone without losing the declaration
        anonymous.Select(s => s.Name).ShouldBe(["Sink"]);
        anonymous.Single(s => s.Name == "Sink").ContainerName.ShouldBeNull();
    }

    [Fact]
    public void Go_SingleDeclarationWhoseValueOpensACall_StillHoistsItsKeyword()
    {
        // the hoist test is structural, not 'does the line end with an open paren': this var declaration
        // spans four lines, has ONE var_spec, and is not a group — so the keyword survives in the
        // signature while the value stays cut at the line, as every first-line signature is
        var rows = Parse("src/hoist.go", """
            package p

            var Registry = New(
                "a",
                "b",
            )

            var (
                Counter int
            )
            """).Symbols;

        rows.Select(s => s.Name).ShouldBe(["Registry", "Counter"]);
        Signature(rows, "Registry").ShouldBe("var Registry = New(");

        // a one-member group is still a group: its head line is just 'var (' and would sign nothing
        Signature(rows, "Counter").ShouldBe("Counter int");
    }

    [Fact]
    public void Go_NonItemNodes_ProduceNoSymbolRows()
    {
        var names = Parse("src/registry.go", ItemsSource).Symbols.Select(s => s.Name).ToList();

        names.Count.ShouldBe(18);
        names.ShouldContain("Registry");

        // struct fields carry a name field of their own and type parameters carry two, but neither is a
        // declaration in the shipped shape — rust leaves its struct fields out too, so go follows the
        // product, not the grammar's convenience
        names.ShouldNotContain("items");
        names.ShouldNotContain("count");
        names.ShouldNotContain("value");
        names.ShouldNotContain("key");
        names.ShouldNotContain("T");
        names.ShouldNotContain("K");
        names.ShouldNotContain("V");

        // the import line and the call targets are not definitions either
        names.ShouldNotContain("fmt");
        names.ShouldNotContain("append");
        names.ShouldNotContain("Println");
        names.ShouldNotContain("package");
        names.ShouldNotContain("registry");
    }

    [Fact]
    public void Go_Chunks_AreBuiltPerSymbolWithMatchingSpans()
    {
        var result = Parse("src/registry.go", ItemsSource);

        result.Symbols.Count.ShouldBe(18);
        result.Chunks.Count.ShouldBe(18);
        foreach (var symbol in result.Symbols)
        {
            var chunk = result.Chunks.Single(c => c.Span == symbol.Span);
            chunk.Text.ShouldContain(symbol.Signature!);
            chunk.TokenCount.ShouldBeGreaterThan(0);
            chunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
        }
    }

    [Fact]
    public void Go_LineComments_AreNotCapturedAsDocumentationYet()
    {
        var symbols = Parse("src/doc.go", """
            package p

            // Run starts the service.
            func Run() int {
                return 1
            }
            """).Symbols;

        // the denominator first: one row was extracted from a file whose doc line sits directly above it.
        // go is the closest grammar to working here — its comment node has the kind CollectComments
        // looks for (unlike python's docstring string node and rust's doc_comment), so the gap is the
        // renderer's '/**'-only rule and nothing else. Owner: BUGS/021, third mechanism
        symbols.Count.ShouldBe(1);
        Find(symbols, "Run", SymbolKind.Function).DocComment.ShouldBeNull();
    }

    [Fact]
    public void Go_BrokenFiles_RecoverDifferentlyPerBreakShape_AndNeverThrow()
    {
        // measured forms, pinned as found: go's recovery is neither rust's (later items always survive)
        // nor python's (everything after the break is swallowed). An unterminated parameter list still
        // closes at the next '}' and the following function comes back; an unclosed BODY runs to the end
        // of file; and a broken declaration group leaves only the member that parsed
        var parameterForm = Parse("src/broken0.go", "package r\n\nfunc bad(a int {\n  return 1\n}\n\nfunc after() int {\n  return 2\n}\n").Symbols;
        parameterForm.Select(s => s.Name).ShouldBe(["bad", "after"]);
        Find(parameterForm, "bad").Span.ShouldBe(new CodeSpan(3, 5));

        var bodyForm = Parse("src/broken1.go", "package r\n\nfunc bad() int {\n  return 1\n\nfunc after() int {\n  return 2\n}\n").Symbols;
        bodyForm.Select(s => s.Name).ShouldBe(["bad"]);
        Find(bodyForm, "bad").Span.ShouldBe(new CodeSpan(3, 9));

        // the group form, and what the alias row costs: 'B = {' parses as a type_alias whose value never
        // closes, so it swallows the function after it (span 5-7, signature cut to 'B =') — recovery got
        // WIDER for the member that parses and correspondingly narrower for what followed. Pinned as
        // found, since keying type_alias (review-023-05 B-3) is what moved these two numbers
        var groupForm = Parse("src/broken2.go", "package r\n\ntype (\n  A int\n  B = {\n\nfunc after() int {\n  return 2\n}\n").Symbols;
        groupForm.Select(s => (s.Name, s.Kind, s.Span.StartLine, s.Span.EndLine))
            .ShouldBe([("A", SymbolKind.Other, 4, 4), ("B", SymbolKind.Other, 5, 7)]);

        // two further measured shapes, stated rather than fixed: a nameless var ('var = 3') contributes
        // no row and the file keeps recovering around it, and an unterminated interface re-parents the
        // NEXT package-level function as an element of that interface — 'after' arrives as
        // Method/container 'I' with the signature 'after() int'. That second one is misattribution by
        // the grammar's error recovery, and it is the shipped behavior of every tree-sitter language here:
        // an error tree is indexed as it parses, warnings included
        var receiverForm = Parse("src/broken3.go",
            "package r\n\nfunc (r *Reg) bad(a int {\n  return 1\n}\n\nfunc after() int {\n  return 2\n}\n").Symbols;
        receiverForm.Select(s => (s.Name, s.Kind, s.ContainerName))
            .ShouldBe([("bad", SymbolKind.Method, "Reg"), ("after", SymbolKind.Function, null)]);
    }

    [Fact]
    public void GoTable_ReadsNothingFromPythonOrRustTrees_AndTheirTablesNothingFromGo()
    {
        using var goGrammar = new Language("Go");
        using var goParser = new Parser(goGrammar);
        using var goTree = goParser.Parse(ItemsSource)!;

        // the guard in the safe direction: the two grammars that share no keyed name with go
        TsSymbolExtractor.Extract(goTree.RootNode!, "python").ShouldBe([]);
        TsSymbolExtractor.Extract(goTree.RootNode!, "rust").ShouldBe([]);
        TsSymbolExtractor.Extract(goTree.RootNode!, "go").Count.ShouldBe(18);
    }

    [Fact]
    public void FunctionDeclarationIsATwin_GoTreeThroughTheJavascriptTableYieldsPartialRows()
    {
        using var goGrammar = new Language("Go");
        using var goParser = new Parser(goGrammar);
        using var goTree = goParser.Parse(ItemsSource)!;

        using var jsGrammar = new Language("JavaScript");
        using var jsParser = new Parser(jsGrammar);
        using var jsTree = jsParser.Parse(JsSource)!;

        // This is the one pair in the feature where a foreign table produces rows rather than silence,
        // and it is recorded as an exposure, not as acceptable behavior: 'function_declaration' is what
        // BOTH grammars call a plain function. Read through javascript's table a go file keeps 3 of its
        // 18 rows — the types, methods, interface methods and package bindings all vanish, and the
        // three survivors lose the containers go derives from a receiver field, so 'Add' would be
        // presented as a top-level function. Per-language tables are the defense against a wrong TABLE
        // for a grammar; nothing at this layer defends a file whose label disagrees with the grammar
        // that parsed it, which is the next test's job (reported as an obstacle for the parent)
        TsSymbolExtractor.Extract(goTree.RootNode!, "javascript").Select(s => s.Name).ShouldBe(["Run", "Map", "anonymous"]);
        TsSymbolExtractor.Extract(goTree.RootNode!, "typescript").Count.ShouldBe(3);
        TsSymbolExtractor.Extract(goTree.RootNode!, "tsx").Count.ShouldBe(3);
        TsSymbolExtractor.Extract(goTree.RootNode!, "go").Count.ShouldBe(18);

        // the symmetric direction, and it is the same twin: javascript's function declaration is a node
        // go's table also keys, so 1 of js's 3 rows survives a go reading while the class and its
        // method — the shapes only js spells differently — disappear
        TsSymbolExtractor.Extract(jsTree.RootNode!, "go").Select(s => s.Name).ShouldBe(["run"]);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "javascript").Count.ShouldBe(3);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "python").ShouldBe([]);
        TsSymbolExtractor.Extract(jsTree.RootNode!, "rust").ShouldBe([]);
    }

    [Fact]
    public void Provider_CouplesEveryRoutedExtensionToOneGrammarAndOneTable()
    {
        var provider = new TreeSitterProvider(Config);
        var accepted = new List<string>();

        // extensions the product recognizes somewhere (LanguageDetector) or that a go/java/typescript
        // developer would try, minus the ones tree-sitter is supposed to own: every one of these must be
        // refused, so the routed set is exactly the list above and nothing else
        var candidates = new[]
        {
            "src/a.cs", "src/Main.csproj", "repo.sln", "repo.slnx", "Directory.props", "x.targets",
            "data.json", "docs/readme.md", "ci/build.yml", "conf/app.yaml", "web/page.html",
            "web/style.css", "db/schema.sql", "scripts/run.sh", "notes.txt", "src/App.java",
            "src/deferred.mts", "src/Model.kt", "stubs/model.pyi", "web/app.vue", "native/main.swift",
            "src/main.c", "src/old.rb", "src/index.php", "cfg/nginx.conf", "bin/tool.exe",
        };

        var pairs = new (string Path, string Grammar, string Label)[]
        {
            ("src/app.js", "JavaScript", "javascript"),
            ("src/app.jsx", "JavaScript", "javascript"),
            ("tools/a.mjs", "JavaScript", "javascript"),
            ("tools/b.cjs", "JavaScript", "javascript"),
            ("src/store.ts", "TypeScript", "typescript"),
            ("src/Badge.tsx", "TSX", "tsx"),
            ("src/lib.rs", "Rust", "rust"),
            ("src/loader.py", "Python", "python"),
            ("cmd/server.go", "Go", "go"),
        };

        // the reason the twin above cannot reach production: the grammar and the table label come from
        // the same extension in the same class, so no file can be parsed by one grammar and read
        // through another language's table. Enumerated here because the pair is the guard — an edit that
        // adds an arm to one switch and not the other is what would make the partial-row case real
        foreach (var (path, grammar, label) in pairs)
        {
            provider.CanHandle(path, null).ShouldBeTrue();
            accepted.Add(Path.GetExtension(path));
            TreeSitterProvider.GrammarName(path).ShouldBe(grammar);
            TreeSitterProvider.LanguageName(path).ShouldBe(label);
            TreeSitterLanguages.Routed(label).ShouldBeTrue();
        }

        // the half the first version lacked: a guard over the extensions ALREADY HANDLED, so a new arm
        // in CanHandle or in either name switch that is not on this list goes red. Without it, adding
        // '.mts' to the detector, to CanHandle and to LanguageName while forgetting GrammarName passed
        // every assertion above — the file would fall back to the javascript grammar while being labeled
        // typescript, which is exactly the mislabel the twin finding is about (measured by review-023-05
        // as their mutant R8 and by mutant MC here as the same drift — see audits/review-023-05-mutation.txt)
        foreach (var path in candidates)
        {
            provider.CanHandle(path, null).ShouldBeFalse($"unexpected handler for {path}");
            accepted.Contains(Path.GetExtension(path)).ShouldBeFalse(
                $"extension {path} became routable without joining the table above");
        }

        // scope worth stating where a future reader looks (review-023-06 N-4): the candidate list covers
        // the extensions the product meets today — every detector-known one tree-sitter must not own, plus
        // look-alikes — so the guard is a list, not an oracle. A language added later with an extension
        // nobody typed here is caught only by the accepted-set equality on the lines below, and only if the
        // person adding it lists it; the refusal sweep cannot imagine an extension on its own
        // the two sets are pinned against each other: an extension losing its arm is as loud as one
        // gaining an undocumented arm
        accepted.Distinct().OrderBy(x => x, StringComparer.Ordinal).ShouldBe(
            pairs.Select(pair => Path.GetExtension(pair.Path)).Distinct().OrderBy(x => x, StringComparer.Ordinal));
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
