using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Fact tests for go references, the mirror of TreeSitterPythonReferenceTests. Every expected row was read
// off the extractor (go reference-shape probes) before the test was written, in the order the walker emits
// it — pre-order, so a type's fields come before the calls in the function bodies below them.
//
// The one thing this file refuses to pretend is that the walker is a wall. 'call_expression' names a call
// in javascript, rust AND go, 'field_declaration' is a struct field in go and in rust, and 'function_decla-
// ration' is a function in go and javascript — so a go tree read under somebody else's label answers with
// rows, not silence (Go_CrossGrammarCallKindIsATwin_AndTheWallIsThePool measures all of it). What keeps a
// go file's edges off another grammar's symbols is the pool, and the last tests here are store-level for
// exactly that reason.
public class TreeSitterGoReferenceTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string FixtureSource = """
        package registry

        import (
            "fmt"
            http "net/http"
            . "strings"
            _ "database/sql"
        )
        import "os"

        type Store struct {
            handler http.Handler
            items   map[string]Item
            list    []Measure
            ptr     *Widget
            gen     Generic[int]
            emb
            fn      func(int) error
        }

        type Set interface {
            Store
            Do() error
        }

        type Alias = Store
        type Named Measure

        func New() *Store {
            s := &Store{}
            cfg := Store{items: map[string]Item{}}
            fmt.Println(s)
            http.HandleFunc("/", nil)
            TrimSpace(" x")
            os.Exit(len(cfg.items))
            helper(1)
            var zero Store
            _ = zero
            return s
        }

        func (s *Store) Put(key string) error {
            fmt.Println(key)
            helper(len(key))
            return nil
        }

        func (p pointer) Get() *Store {
            return p.store
        }

        type pointer struct{ store *Store }

        func helper(n int) int {
            return n
        }
        """;

    [Fact]
    public void Go_ImportTree_EmitsOneRowPerBindingOfEveryShape()
    {
        var imports = Extract(FixtureSource).Where(r => r.Kind == RefKind.Import).ToArray();

        // four rows from two statements, and the binding rule is go's own, read off tree-sitter-go rather
        // than borrowed: 'fmt' binds the LAST element of its path (a go file types 'fmt.Println', never
        // 'net'), 'http "net/http"' binds the ALIAS it writes, '. "strings"' names no local at all so it
        // anchors on the package the way a starred python import and a globbed rust use do, and the single
        // 'import "os"' outside the group behaves exactly like a grouped one
        imports.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("fmt", "fmt", RefKind.Import, 3),
            ("net/http", "http", RefKind.Import, 3),
            ("strings", "strings", RefKind.Import, 3),
            ("os", "os", RefKind.Import, 9),
        ]);
        imports.ShouldAllBe(r => r.EnclosingName == null);

        // grouped specs all report the statement's line, not the spec's — the same choice python's
        // 'import os, sys' makes, so an edge never claims a line the import keyword is not on
        imports.Count(r => r.Line == 3).ShouldBe(3);
    }

    [Fact]
    public void Go_BlankImport_EmitsNoBinding_WhileItsGroupNeighboursLand()
    {
        var imports = Extract(FixtureSource).Where(r => r.Kind == RefKind.Import).ToList();

        // '_ "database/sql"' is a side-effect import: the file types nothing named 'sql' and never will,
        // so there is no binding to record. This is the same blank the symbol row refuses (B-1) and the
        // blank import the parent's contract predicted — and it is the third name-kind in one group
        // (package_identifier alias, dot, blank_identifier), which is why the reader switches on the KIND
        // of the name field rather than on its presence
        imports.ShouldNotContain(r => r.ToName == "sql" || r.ToSymbolId == "database/sql");

        // the control that keeps the silence a rule and not a broken walk: the blank's own group is the
        // source of three of the four rows above
        imports.Count.ShouldBe(4);
    }

    [Fact]
    public void Go_UnaliasedMultiSegmentImport_BindsTheLastPathElement_NotTheRepoRoot()
    {
        var refs = Extract("cmd/use.go", """
            package p

            import "encoding/json"

            func Use() {
                json.Marshal(1)
            }
            """).ToList();

        // the shape the aliased fixture cannot test: with no name field, the binding has to be DERIVED from
        // the path, and go derives it from the LAST element — the file types 'json.Marshal' and never
        // 'encoding'. That is the exact inverse of python's rule ('import a.b.c' binds the TOP package), so
        // the two rows in the home now disagree on purpose, each read off its own grammar. A reader that
        // took the first segment would still name the import row 'encoding/json' in the column and would
        // pass every aliased case, which is how this hole got found (mutant p12, first run)
        refs.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("encoding/json", "json", RefKind.Import, 3),
            (null, "Marshal", RefKind.Call, 6),
        ]);
        refs.ShouldNotContain(r => r.ToName == "encoding");

        // and the negative control from the other grammar, so the divergence is stated rather than implied:
        // the same dotted-path idea in python binds the top package
        using var pythonGrammar = new Language("Python");
        using var pythonParser = new Parser(pythonGrammar);
        using var pythonTree = pythonParser.Parse("import a.b.c\n")!;
        var pythonRow = TsReferenceExtractor.Extract(pythonTree.RootNode!, "python", "src/x.py")
            .ShouldHaveSingleItem();
        pythonRow.ToName.ShouldBe("a");
        pythonRow.ToSymbolId.ShouldBe("a.b.c");
    }

    [Fact]
    public void Go_MultiSlashImportPath_BindsTheFinalElement_AfterEveryDirectoryHop()
    {
        var refs = Extract("cmd/use.go", """
            package p

            import (
                "github.com/go-chi/chi"
                http "net/http"
            )

            func Use() {
                chi.NewRouter()
                http.HandleFunc("/", nil)
            }
            """).ToList();

        // 'github.com/go-chi/chi' is the shape that makes the rule testable at all: with one slash only,
        // reading the FIRST and the LAST segment answers the same string, and a mutant flipping the rule
        // walks green (that is exactly what happened on the first matrix pass — p12 survived). With three
        // hops the difference is 'github' against 'chi', and go's answer is the last one, because that is
        // the package name the file then types. The aliased sibling in the same group still wins outright
        refs.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("github.com/go-chi/chi", "chi", RefKind.Import, 3),
            ("net/http", "http", RefKind.Import, 3),
            (null, "NewRouter", RefKind.Call, 9),
            (null, "HandleFunc", RefKind.Call, 10),
        ]);
        refs.Where(r => r.ToName == "github" || r.ToName == "com" || r.ToName == "go-chi").ShouldBeEmpty();

        // and the module column keeps the whole quoted path, so the identity of the imported package stays
        // readable even though the binding is one of its segments — 'net/http' aliased to 'http' proves the
        // column is the path and not the alias
        refs[0].ToSymbolId.ShouldBe("github.com/go-chi/chi");
        refs[1].ToSymbolId.ShouldBe("net/http");
    }

    [Fact]
    public void Go_LocalQualifiedAndDotImportedCalls_EmitOneRowEachNamedAfterTheCallee()
    {
        var calls = Extract(FixtureSource).Where(r => r.Kind == RefKind.Call).ToArray();

        // 'fmt.Println' is a selector_expression and the edge names its FIELD, not the package — an agent
        // searches for 'Println'. 'helper(1)' is a bare identifier and names itself. 'TrimSpace' arrives
        // bare because a dot import put it in scope. 'os.Exit' behaves like 'fmt.Println'. And 'len' is a
        // builtin nothing in the pool can answer: it stays a row, the way python keeps 'super' and rust
        // keeps unresolved paths, because dropping it would decide for the reader what counts as a name
        calls.Select(r => (r.ToName, r.Line, r.EnclosingName)).ShouldBe(
        [
            ("Println", 32, "New"),
            ("HandleFunc", 33, "New"),
            ("TrimSpace", 34, "New"),
            ("Exit", 35, "New"),
            ("len", 35, "New"),
            ("helper", 36, "New"),
            ("Println", 43, "Store.Put"),
            ("helper", 44, "Store.Put"),
            ("len", 44, "Store.Put"),
        ]);
        calls.ShouldAllBe(r => r.ToSymbolId == null);
    }

    [Fact]
    public void Go_TypeRelations_EmitReferenceAndInheritPerPosition()
    {
        var targets = Extract(FixtureSource)
            .Where(r => r.Kind is RefKind.Reference or RefKind.Inherit)
            .ToArray();

        // every row here is a position where a declaration USES another named type, and the kind tells
        // which relation: a nameless struct field is go's embedding and an interface's bare element is the
        // same thing, so both get Inherit the way a python base class does; a named field, an alias body
        // and a defined-by-name type are uses. A qualified target keeps its package in the module column
        // ('http.Handler' → column 'http', name 'Handler') — the shape javascript's named imports use
        targets.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("http", "Handler", RefKind.Reference, 12),
            (null, "Widget", RefKind.Reference, 15),
            (null, "Generic", RefKind.Reference, 16),
            (null, "emb", RefKind.Inherit, 17),
            (null, "Store", RefKind.Inherit, 22),
            (null, "Store", RefKind.Reference, 26),
            (null, "Measure", RefKind.Reference, 27),
            (null, "Store", RefKind.Reference, 52),
        ]);
    }

    [Fact]
    public void Go_DefiningTypeSpecs_AggregatesAndFunctionTypes_EmitNoEdges()
    {
        var refs = Extract(FixtureSource).ToList();

        // 'type Store struct{…}' and 'type Set interface{…}' DEFINE: their bodies are read field by field
        // (that is where the rows above come from), so the definition itself signs nothing on top of them
        refs.ShouldNotContain(r => r.Line == 11 || r.Line == 21);

        // element types inside aggregates and function types are deliberately out of scope: a field of
        // type 'map[string]Item' is not a use of 'Item' in the sense a reader of the store wants, and
        // unwrapping '[]Measure', 'chan X' or 'func(int) error' would fill the pool with builtins. So the
        // fence is stated by POSITION, not by name — 'Measure' really does appear in this file, at line 27,
        // where 'type Named Measure' uses it by name; what must stay silent are lines 13 ('map[string]Item'),
        // 14 ('[]Measure') and 18 ('func(int) error'), and 'Item' never appears at all because the only
        // places go writes it here are inside a map and inside a composite literal
        refs.Where(r => r.Line == 13 || r.Line == 14 || r.Line == 18).ShouldBeEmpty();
        refs.Where(r => r.ToName == "Item").ShouldBeEmpty();
        refs.Single(r => r.ToName == "Measure").Line.ShouldBe(27);

        // the control inside the same struct: '*Widget' — a pointer, which the reader DOES unwrap because
        // it names exactly one type — arrives at line 15, and 'Generic[int]' at 16 names 'Generic' while
        // its type argument stays inside type_arguments and unnamed. Naming that second half explicitly,
        // because 'int' is the row a type_arguments element WOULD produce: measured, tree-sitter-go spells
        // a generic instantiation argument as type_elem — the same kind an embedded interface element is —
        // and only the parent check tells 'embeds a type' from 'instantiates with one'
        refs.ShouldContain(r => r.ToName == "Widget" && r.Line == 15);
        refs.Where(r => r.ToName == "int").ShouldBeEmpty();

        // the same holds for a type written in a signature or a var declaration: '*Store' as New's result
        // and 'Store' as the type of 'var zero' name no relation the index claims to model, matching the
        // silence rust and python keep about their own parameter and annotation positions
        refs.Where(r => r.ToName == "Store" && (r.Line == 29 || r.Line == 37)).ShouldBeEmpty();
    }

    [Fact]
    public void Go_CompositeLiteralsAndBlankFields_EmitNothing_WhileNamedNeighboursDo()
    {
        var refs = Extract(FixtureSource).ToList();

        // '&Store{}' and 'Store{items: …}' are go's construction shape — the node that stands where rust
        // writes 'Registry { … }' and javascript 'new Ledger()'. The grammar gives composite_literal an
        // addressable 'type' field (measured: type_identifier 'Store' under it), so naming an edge here was
        // a choice available to us and declined on purpose: the other two grammars sign nothing for
        // construction, and a go-only rule would make 'who references Store' answer differently per grammar
        // for the same intent. Silence is the symmetry rule, pinned here rather than left implied
        refs.ShouldNotContain(r => r.Line == 30 || r.Line == 31);

        // a blank field name ('_ int') discards the slot — it binds nothing and embeds nothing — so it
        // signs no edge, and a field listing two names for one type is ONE edge: the grammar's name field
        // is only the first identifier, and a per-name reading would sign the same span twice (B-2 on the
        // symbol side, applied here to a use). Both are measured in GoReferenceRules_ReadFieldShapes…, and
        // the control is again the file's own named field, which does arrive
        refs.ShouldContain(r => r.ToName == "Handler" && r.Line == 12);
        Extract("type T struct {\n\t_ int\n\ta, b Base\n\tsvc http.Handler\n}\n\ntype Base struct{}\n")
            .Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            [
                (null, "Base", RefKind.Reference, 3),
                ("http", "Handler", RefKind.Reference, 4),
            ]);
    }

    [Fact]
    public void Go_AnonymousInterfaceAndStructFieldTypes_EmitNoEdgeForTheShapeThemselves()
    {
        var refs = Extract("cmd/outer.go", """
            package p

            type Outer struct {
                Dep interface {
                    Do() error
                }
                Buf struct {
                    X int
                }
                Fn func() error
            }

            type Anon = interface{ Read() }

            var Sink interface{ Write() }

            type Named interface {
                Anon
            }

            """).ToList();

        // the fence for shapes that have a NAME and a type position at once. A field of an anonymous
        // interface type declares a slot, not a use of any named type — there is no type to point at — so
        // nothing is signed for 'Dep', 'Buf' or 'Fn', and the same refusal covers 'type Anon = interface{…}'
        // (an alias DEFINING an anonymous shape) and the package-level 'var Sink interface{…}' (a variable,
        // not a type relation). The rows that DO belong here are the ones naming a real type: the inner 'X
        // int' field is read on its own account, and an interface element naming a declared type is
        // embedding. Two guards refuse the anonymous shapes and they refuse different paths: the name reader
        // answers no name for an interface_type or a struct_type, which is what holds the FIELD cases here,
        // while the define guard in the reader's type_spec/type_alias branch covers the alias. Mutant n01
        // (define guard dropped) is survivable because the name reader still refuses; mutant n04 (name
        // reader made to name an interface_type) is killed by the 'Dep' line below
        refs.Select(r => (r.ToName, r.Kind, r.Line))
            .ShouldBe([("int", RefKind.Reference, 8), ("Anon", RefKind.Inherit, 18)]);
        refs.Where(r => r.ToName == "Do" || r.ToName == "Read" || r.ToName == "Write")
            .ShouldBeEmpty();
        refs.ShouldNotContain(r => r.ToName == "interface");
    }

    [Fact]
    public void Go_AliasToAnonymousInterface_HasNoOtherRefusalOnItsPath()
    {
        var refs = Extract("cmd/alias.go", """
            package p

            type Anon = interface{ Read() }
            """);

        // the file above carries exactly one type position, and it is the one the define guard in the
        // type_spec/type_alias branch owns: nothing else in it could answer for the silence. The companion
        // test pins the anonymous shapes together with a field of an anonymous interface type, where the
        // name reader's refusal covers the field path and cannot be told apart from the guard's. This one
        // separates them: mutant n01 (guard dropped) stays green on it because the reader still answers no
        // name for an interface_type, mutant n04 (reader made to name one) stays green because the guard
        // never calls the reader here — only the pair (n01b) produces a row, which is what proves the
        // guard is live code and not decoration
        refs.ShouldBeEmpty();
    }

    [Fact]
    public void Go_BrokenImportShapes_NeverLandAnUnexpectedNodeInTheNameField()
    {
        var dottedAlias = Extract("cmd/a.go", "package p\nimport net/http \"x/y\"\n");
        var numericAlias = Extract("cmd/b.go", "package p\nimport 5 \"x/y\"\n");
        var secondString = Extract("cmd/c.go", "package p\nimport \"x/y\" \"z\"\n");
        var blankInGroup = Extract("cmd/d.go", "package p\nimport (\n\t_ _ \"a\"\n\t\"b\"\n)\n");
        var pathlessBlank = Extract("cmd/e.go", "package p\nimport _ x\n");

        // the import reader switches on the KIND of the name field and falls through to null for anything
        // it does not list, so the question this battery answers is what the grammar can actually put in
        // that field once a file stops being legal go. Measured over five broken shapes: recovery either
        // keeps a legal name node ('net/http' degrades to ERROR('net/') plus the surviving
        // package_identifier 'http', so the binding is still the last element of the alias, read from the
        // node that survived rather than from the broken one), or drops the field entirely ('5' sits in an
        // ERROR sibling, so the spec reads as an unaliased import and binds the package of the path). The
        // blank without a path parses as no import_spec at all — nothing to refuse, nothing to bind
        dottedAlias.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            [("x/y", "http", RefKind.Import, 2)]);
        numericAlias.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            [("x/y", "y", RefKind.Import, 2)]);
        secondString.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            [("x/y", "y", RefKind.Import, 2)]);
        blankInGroup.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            [("b", "b", RefKind.Import, 2)]);
        pathlessBlank.ShouldBeEmpty();

        // no shape here produces a name field outside blank_identifier, dot, package_identifier or none,
        // which is the reachability statement for the switch's fallthrough: mutant p26 (fallthrough made to
        // bind the package) survives because the only kind that would reach it is blank_identifier, already
        // refused two lines earlier — p10b, which removes both refusals, is the measurement that says the
        // fallthrough is load-bearing rather than dead
        Extract("cmd/f.go", "package p\nimport _ \"x/y\"\n").ShouldBeEmpty();
    }

    [Fact]
    public void Go_EnclosingNameIsTheSymbolContainerChainByteForByte()
    {
        var result = Parse("cmd/registry.go", FixtureSource);
        var put = result.Symbols.Single(s => s.Name == "Put");

        // the trap this round was built for: go hangs a method on its type through the RECEIVER field, not
        // through an enclosing node, so an ancestor walk cannot see the owner and would report a bare 'Put'
        // while the symbol row it stands in reads container 'Store', name 'Put'. Both sides now lift the
        // same seam, and the reference column has to equal the row's own chain byte for byte
        put.Kind.ShouldBe(SymbolKind.Method);
        put.ContainerName.ShouldBe("Store");
        var calls = result.Refs.Where(r => r.Kind == RefKind.Call && r.Line == 43).ToList();
        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(r => r.EnclosingName == $"{put.ContainerName}.{put.Name}");
        calls[0].EnclosingName.ShouldBe("Store.Put");

        // a pointer receiver unwraps to the same string the type's own row is named by, and an ordinary
        // function encloses as its own name with no dot — the two shapes the chain has to keep distinct
        var listen = Parse("cmd/other.go", """
            package main

            func New() *Store {
                return load()
            }

            func (s *Store) Save() error {
                return load()
            }

            type Store struct{}
            """);
        listen.Refs.Where(r => r.ToName == "load").Select(r => r.EnclosingName).ShouldBe(["New", "Store.Save"]);
        listen.Symbols.Single(s => s.Name == "Save").ContainerName.ShouldBe("Store");

        // and the invariant stated for the whole file instead of the two picked rows: every call written
        // inside a method reports its container prefix, every call inside a plain function does not
        foreach (var symbol in result.Symbols.Where(s => s.Kind == SymbolKind.Method))
        {
            // a method with no calls in it (Get reads a field, and a field read is no one's callee) is a
            // vacuous pass here, which is why the rows are counted where they exist: Put's three, in the
            // second half of this test, are what makes the loop mean something
            var inside = result.Refs.Where(r =>
                r.Kind == RefKind.Call && r.Line >= symbol.Span.StartLine && r.Line <= symbol.Span.EndLine).ToList();
            inside.ShouldAllBe(r => r.EnclosingName == $"{symbol.ContainerName}.{symbol.Name}");
        }

        foreach (var symbol in result.Symbols.Where(s => s.Kind == SymbolKind.Function))
        {
            result.Refs.Where(r =>
                    r.Kind == RefKind.Call && r.Line >= symbol.Span.StartLine && r.Line <= symbol.Span.EndLine)
                .ShouldAllBe(r => r.EnclosingName == symbol.Name);
        }
    }

    [Fact]
    public void Go_CrossGrammarCallKindIsATwin_AndTheWallIsThePool()
    {
        using var goGrammar = new Language("Go");
        using var jsGrammar = new Language("JavaScript");
        using var rustGrammar = new Language("Rust");
        using var pyGrammar = new Language("Python");
        using var goParser = new Parser(goGrammar);
        using var jsParser = new Parser(jsGrammar);
        using var rustParser = new Parser(rustGrammar);
        using var pyParser = new Parser(pyGrammar);
        using var goTree = goParser.Parse(FixtureSource)!;
        using var jsTree = jsParser.Parse("import { mul } from './util';\n\nexport function run(items) {\n  return mul(items.length, 2);\n}\n")!;
        using var rustTree = rustParser.Parse("pub struct Registry {\n    entries: HashMap<String, String>,\n}\n\nimpl Registry {\n    pub fn add(&mut self) {\n        helper(1);\n    }\n}\n")!;
        using var pyTree = pyParser.Parse("import os\n\nclass Holder(Base):\n    def load(self, path):\n        return os.path.join(path)\n")!;

        // measured, not asserted as a wish: a go tree read through javascript's rules harvests nine call
        // rows — every call_expression in the file, because that is what a call is spelled in both
        // grammars — and through rust's rules the same nine for the same reason. The walker is NOT a wall,
        // and any test claiming cross-grammar silence on a twin kind would be measuring a coincidence
        TsReferenceExtractor.Extract(goTree.RootNode!, "go", "src/x.go").Count.ShouldBe(21);
        var goUnderJs = TsReferenceExtractor.Extract(goTree.RootNode!, "javascript", "src/x.go");
        goUnderJs.Count.ShouldBe(9);
        goUnderJs.ShouldAllBe(r => r.Kind == RefKind.Call);

        // and the enclosing column degrades exactly as the container lists predict: 'function_declaration'
        // is a twin, so the six rows written inside 'func New' keep their 'New'; 'method_declaration' is
        // go's own kind and invisible to javascript's containers, so the three inside the method answer
        // null instead of 'Store.Put'. Nothing here is a bug to fix — it is the evidence that the pool is
        // the firewall and the walker never was
        goUnderJs.Count(r => r.EnclosingName == "New").ShouldBe(6);
        goUnderJs.Count(r => r.EnclosingName == null).ShouldBe(3);
        var goUnderRust = TsReferenceExtractor.Extract(goTree.RootNode!, "rust", "src/x.go");
        goUnderRust.Count.ShouldBe(9);
        goUnderRust.ShouldAllBe(r => r.EnclosingName == null);

        // and in the other direction: go's rules over a javascript tree take the same calls (the twin is
        // symmetric) and lose the import, which javascript spells 'import_statement' and go does not read
        var jsUnderGo = TsReferenceExtractor.Extract(jsTree.RootNode!, "go", "src/run.js");
        jsUnderGo.Select(r => (r.ToSymbolId, r.ToName, r.Kind)).ShouldBe(
        [
            (null, "mul", RefKind.Call),
        ]);
        var jsOwn = TsReferenceExtractor.Extract(jsTree.RootNode!, "javascript", "src/run.js");
        jsOwn.Select(r => (r.ToSymbolId, r.ToName, r.Kind)).ShouldBe(
        [
            ("./util", "mul", RefKind.Import),
            (null, "mul", RefKind.Call),
        ]);

        // rust shares 'field_declaration' with go as well, which is the fourth twin kind this feature
        // measured (function_declaration, call_expression, attribute, argument_list before it): a rust
        // struct field read under go's label becomes a Reference row go never intended to sign
        var rustUnderGo = TsReferenceExtractor.Extract(rustTree.RootNode!, "go", "src/lib.rs");
        rustUnderGo.ShouldContain(r => r.Kind == RefKind.Reference && r.ToName == "HashMap");
        rustUnderGo.ShouldNotContain(r => r.Kind == RefKind.Import);

        // python is the one grammar whose call shape shares no name with the others ('call', not
        // 'call_expression'), and the measurement shows it: nothing reaches it from a go tree and nothing
        // reaches go's rules from a python tree
        TsReferenceExtractor.Extract(goTree.RootNode!, "python", "src/x.go").ShouldBe([]);
        TsReferenceExtractor.Extract(pyTree.RootNode!, "go", "src/x.py").ShouldBe([]);

        // so the real wall is the pool, and these rows — harvested from a tree under a label that is not
        // its grammar — could only ever bind symbols inside the pool their label names. That is what the
        // store-level tests below pin, and what an inlined pool would break
        TreeSitterLanguages.ReferencePoolFor("go").ShouldNotBe(TreeSitterLanguages.ReferencePoolFor("javascript"));
        TreeSitterLanguages.ReferencePoolFor("go").ShouldNotBe(TreeSitterLanguages.ReferencePoolFor("rust"));
        TreeSitterLanguages.ReferencePoolFor("go").ShouldNotBe(TreeSitterLanguages.ReferencePoolFor("python"));
    }

    [Fact]
    public void Index_GoCallToNameDefinedOnlyInJavascript_BindsNothingAcrossTheWall()
    {
        var workspace = NewGoWorkspace();
        WriteFile(workspace, "repo/cmd/caller.go", "package main\n\nfunc run(items []int) int {\n\treturn render(1)\n}\n");
        WriteFile(workspace, "repo/src/view.js", "export function render(n) {\n  return n;\n}\n\nexport function use() {\n  return render(2);\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var goFile = store.ListFiles(repoId).Single(f => f.Path == "cmd/caller.go");
        var jsFile = store.ListFiles(repoId).Single(f => f.Path == "src/view.js");

        // the edge exists — go's call rule saw render(1) — and resolves to nothing, because javascript's
        // 'render' row lives in the js-ts pool and go's pool holds only go files
        var call = store.ListRefsByFile(goFile.Id).Single(r => r.Kind == "call" && r.ToName == "render");
        call.FromSymbolId.ShouldBe(0);
        call.EnclosingSymbol.ShouldBe("run");

        // the control: javascript reaches its own render from its own file
        var jsRender = store.ListSymbolsByFile(jsFile.Id).Single(s => s.Name == "render");
        store.ListRefsByFile(jsFile.Id).Where(r => r.ToName == "render" && r.Kind == "call")
            .ShouldAllBe(r => r.FromSymbolId == jsRender.Id);
    }

    [Fact]
    public void Index_JavascriptCallToNameDefinedOnlyInGo_BindsNothingAcrossTheWall()
    {
        var workspace = NewGoWorkspace();
        WriteFile(workspace, "repo/cmd/settle.go", "package main\n\nfunc settle(n int) int {\n\treturn n\n}\n\nfunc use() {\n\tsettle(1)\n}\n");
        WriteFile(workspace, "repo/src/probe.js", "export function run(items) {\n  return settle(items.length);\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var goFile = store.ListFiles(repoId).Single(f => f.Path == "cmd/settle.go");
        var jsFile = store.ListFiles(repoId).Single(f => f.Path == "src/probe.js");

        // the twin kind cuts both ways — javascript's call_expression and go's are the same word — so the
        // direction that matters is the store one: a js edge may not bind a go row just because the name fits
        store.ListRefsByFile(jsFile.Id).Single(r => r.ToName == "settle").FromSymbolId.ShouldBe(0);
        var goSettle = store.ListSymbolsByFile(goFile.Id).Single(s => s.Name == "settle");
        store.ListRefsByFile(goFile.Id).Where(r => r.ToName == "settle" && r.Kind == "call")
            .ShouldAllBe(r => r.FromSymbolId == goSettle.Id);
    }

    [Fact]
    public void Index_GoAndRustEdgesStayInTheirPools_WhereBothGrammarsSpellRootAndFieldAlike()
    {
        var workspace = NewGoWorkspace();
        WriteFile(workspace, "repo/cmd/parse.go", "package main\n\nfunc parse(n int) int {\n\treturn n\n}\n\nfunc use() {\n\tparse(1)\n}\n");
        WriteFile(workspace, "repo/src/lib.rs", "pub fn marshal(n: u32) -> u32 {\n    n\n}\n\npub fn ruse() {\n    marshal(1);\n    parse(2);\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var goFile = store.ListFiles(repoId).Single(f => f.Path == "cmd/parse.go");
        var rustFile = store.ListFiles(repoId).Single(f => f.Path == "src/lib.rs");

        // rust's 'parse(2)' names a go function and gets an unresolved row; go's 'parse(1)' resolves to the
        // go row. Same file, two edges, one wall — which is the pair to trust, because rust and go share
        // BOTH a root kind ('source_file') and a field kind ('field_declaration'), so a guard that leaned on
        // either of those names would not separate them and the pool does
        var goParse = store.ListSymbolsByFile(goFile.Id).Single(s => s.Name == "parse");
        store.ListRefsByFile(goFile.Id).Single(r => r.ToName == "parse").FromSymbolId.ShouldBe(goParse.Id);
        store.ListRefsByFile(rustFile.Id).Single(r => r.ToName == "parse").FromSymbolId.ShouldBe(0);

        var rustMarshal = store.ListSymbolsByFile(rustFile.Id).Single(s => s.Name == "marshal");
        store.ListRefsByFile(rustFile.Id).Single(r => r.ToName == "marshal").FromSymbolId.ShouldBe(rustMarshal.Id);
        store.ListRefsByFile(goFile.Id).ShouldNotContain(r => r.ToName == "marshal");
    }

    [Fact]
    public void Index_GoAndPythonEdgesStayInTheirPools_AtBothEndsOfTheName()
    {
        var workspace = NewGoWorkspace();
        WriteFile(workspace, "repo/cmd/load.go", "package main\n\nfunc bootstrap(n int) int {\n\treturn n\n}\n\nfunc use() {\n\thelper(1)\n}\n\nfunc helper(n int) int {\n\treturn n\n}\n");
        WriteFile(workspace, "repo/src/helper.py",
            "def helper(n):\n    return n\n\ndef use():\n    helper(2)\n    return bootstrap(1)\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var goFile = store.ListFiles(repoId).Single(f => f.Path == "cmd/load.go");
        var pyFile = store.ListFiles(repoId).Single(f => f.Path == "src/helper.py");

        // 'helper' is defined in both grammars AND called in both: each edge binds its own file's row, and
        // the go pool never sees python's. This is the collision the pool exists for, with two same-named
        // rows actually present rather than a name nobody declares
        var goHelper = store.ListSymbolsByFile(goFile.Id).Single(s => s.Name == "helper");
        var pyHelper = store.ListSymbolsByFile(pyFile.Id).Single(s => s.Name == "helper");
        goHelper.Id.ShouldNotBe(pyHelper.Id);
        store.ListRefsByFile(goFile.Id).Single(r => r.ToName == "helper").FromSymbolId.ShouldBe(goHelper.Id);
        store.ListRefsByFile(pyFile.Id).Single(r => r.ToName == "helper").FromSymbolId.ShouldBe(pyHelper.Id);

        // and the two cross-name edges stay unresolved rows: go's 'bootstrap' is invisible to python's file
        // even though the name is exactly one go function defines
        store.ListRefsByFile(pyFile.Id).Single(r => r.ToName == "bootstrap").FromSymbolId.ShouldBe(0);
    }

    [Fact]
    public void Index_FourPoolsOneName_EachEdgeBindsOnlyItsOwnGrammar()
    {
        var workspace = NewGoWorkspace();
        WriteFile(workspace, "repo/cmd/run.go", "package main\n\nfunc run(n int) int {\n\treturn n\n}\n\nfunc goUse() {\n\trun(1)\n}\n");
        WriteFile(workspace, "repo/src/run.js", "export function run(n) {\n  return n;\n}\n\nexport function jsUse() {\n  return run(2);\n}\n");
        WriteFile(workspace, "repo/src/run.rs", "pub fn run(n: u32) -> u32 {\n    n\n}\n\npub fn rustUse() {\n    run(3);\n}\n");
        WriteFile(workspace, "repo/src/run.py", "def run(n):\n    return n\n\ndef py_use():\n    return run(4)\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var rows = store.ListFiles(repoId)
            .Where(f => f.Path.Contains("run.", StringComparison.Ordinal))
            .ToDictionary(f => f.Path, f => store.ListSymbolsByFile(f.Id).Single(s => s.Name == "run"));

        // one name, four rows, four pools: every file's 'run' edge binds the row in its own grammar and
        // only that one — the shape that three pools pinned and the fourth had to keep
        rows.Count.ShouldBe(4);
        foreach (var (path, symbol) in rows)
        {
            var edges = store.ListRefsByFile(store.ListFiles(repoId).Single(f => f.Path == path).Id)
                .Where(r => r.ToName == "run").ToList();
            edges.ShouldHaveSingleItem($"run edge in {path}");
            edges[0].FromSymbolId.ShouldBe(symbol.Id, path);
        }
    }

    [Fact]
    public void Go_FileWithoutTrailingNewline_SitsInTheErrorState_AtATypeBodiedDeclaration()
    {
        const string NoNewline = "package p\n\ntype Set interface {\n\tStore\n}";
        const string WithNewline = NoNewline + "\n";
        const string FuncTail = "package p\n\nfunc Load() {\n\thelper()\n}";

        using var grammar = new Language("Go");
        using var parser = new Parser(grammar);
        using var truncated = parser.Parse(NoNewline)!;
        using var terminated = parser.Parse(WithNewline)!;
        using var funcTailTruncated = parser.Parse(FuncTail)!;

        // measured, and it cost a fixture to find: a go file whose LAST declaration closes a multi-line type
        // body and which ends without a newline comes back with HasError on the root and no ERROR or MISSING
        // node anywhere under it — tree-sitter-go's scanner wants the line terminator before it accepts the
        // closing brace. Add the newline and the same bytes parse clean; end the file with a function body
        // instead and it does not trip either, so this is not 'any file without a final newline'
        truncated.RootNode!.HasError.ShouldBeTrue();
        Collect(truncated.RootNode!).Where(x => x.Type == "ERROR" || x.Type == "MISSING").ShouldBeEmpty();
        terminated.RootNode!.HasError.ShouldBeFalse();
        funcTailTruncated.RootNode!.HasError.ShouldBeFalse();

        // and the rows survive it, because the index never asks the flag: the truncated file still yields the
        // embedding edge, identical to the terminated one. Worth pinning for T6 — a repository file saved
        // without a final newline must not be treated as data the index lost
        TsReferenceExtractor.Extract(truncated.RootNode!, "go", "src/set.go")
            .Select(r => (r.ToName, r.Kind))
            .ShouldBe(TsReferenceExtractor.Extract(terminated.RootNode!, "go", "src/set.go")
                .Select(r => (r.ToName, r.Kind)));
    }

    [Fact]
    public void Go_ReferenceRows_CarryTheSourcePathOfTheirFile()
    {
        Extract("cmd/store.go", FixtureSource).ShouldAllBe(r => r.FromPath == "cmd/store.go");
    }

    private static IReadOnlyList<SymbolRef> Extract(string source) =>
        Extract("cmd/store.go", source);

    private static IReadOnlyList<SymbolRef> Extract(string path, string source)
    {
        using var grammar = new Language("Go");
        using var parser = new Parser(grammar);
        using var tree = parser.Parse(source)!;
        return TsReferenceExtractor.Extract(tree.RootNode!, "go", path);
    }

    private static ParseResult Parse(string path, string text) =>
        new TreeSitterProvider(Config).Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(text));

    private static string NewGoWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static List<Node> Collect(Node node)
    {
        var nodes = new List<Node> { node };
        foreach (var child in node.Children)
        {
            nodes.AddRange(Collect(child));
        }

        return nodes;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
