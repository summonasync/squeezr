using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing.TreeSitter;

// GoMini through the whole pipeline: one module, two subpackage directories, a cmd main, and the same
// javascript twin trick the python crate plays, so the pool wall and the per-symbol chunk ratio are read
// from one store. Counts were produced by indexing the fixture (the dump sits next to the review
// carrier), and the refusals below are the grammar's own, restated at product scale with real files.
//
// Doc comments are not claimed anywhere in this class: go's '//' shape is one of the three deferred
// capture mechanisms, and a fixture row that looked like documentation coverage would say more than the
// product ships.
public class GoFixtureEndToEndTests
{
    private const string JsTwinSource = """
        export class Registry {
          add(item) {
            return item;
          }
        }

        export function normalize(value) {
          return value.trim();
        }

        export const DEFAULT_WIDTH = 40;
        """;

    private static readonly string[] GoFiles =
    [
        "cmd/report/main.go",
        "internal/registry/registry.go",
        "internal/registry/store.go",
        "internal/text/text.go",
    ];

    [Fact]
    public void Index_GoMiniModule_PersistsEveryFileAsGoCodeWithPerSymbolChunks()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        // the module file is data: it exists so the import paths in the sources mean something, and it is
        // indexed as a docs chunk exactly like Cargo.toml and pyproject.toml are
        files["go.mod"].Kind.ShouldBe("docs");
        files["go.mod"].Language.ShouldBe("unknown");
        GoFiles.ShouldAllBe(path => files[path].Language == "go" && files[path].Kind == "code");

        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in GoFiles)
        {
            var fileSymbols = store.ListSymbolsByFile(files[path].Id);
            var fileChunks = store.ListChunksByFile(files[path].Id);
            symbols[path] = fileSymbols.Count;

            fileChunks.Count.ShouldBe(fileSymbols.Count);
            fileChunks.ShouldAllBe(c => c.SymbolId != null);
            foreach (var symbol in fileSymbols)
            {
                fileChunks.ShouldContain(c => c.StartLine == symbol.StartLine && c.EndLine == symbol.EndLine);
            }

            fileChunks.ShouldAllBe(c => c.VectorStatus == "ok" && c.ModelKey != null);
        }

        symbols.ShouldBe(new Dictionary<string, int>
        {
            ["cmd/report/main.go"] = 1,
            ["internal/registry/registry.go"] = 22,
            ["internal/registry/store.go"] = 9,
            ["internal/text/text.go"] = 5,
        });
    }

    [Fact]
    public void SymbolLookup_GoMethod_NamesTheReceiverTypeAsItsContainer()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        var hit = new SymbolLookup(workspace)
            .Find("Put", null, null, 400, body: false)
            .Matches.ShouldHaveSingleItem();

        hit.Repo.ShouldBe("demo");
        hit.File.ShouldBe("internal/registry/store.go");
        hit.Kind.ShouldBe("method");
        hit.Signature.ShouldBe("func (s *Store) Put(key string) error");

        // a go method's owner comes out of its receiver field, never out of an ancestor node, and the same
        // string names the type's own row — which is what lets a reader pair Store.Put across files
        hit.ContainerName.ShouldBe("Store");

        var generic = new SymbolLookup(workspace)
            .Find("Get", null, null, 400, body: false)
            .Matches.ShouldHaveSingleItem();
        generic.ContainerName.ShouldBe("Generic");
        generic.Signature.ShouldBe("func (g *Generic[T]) Get(key string) T");
    }

    [Fact]
    public void Index_GoImportShapes_BindTheLastPathElement_AndNoRowNamesAPackageDirectory()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var rows = TsFixtureHarness.ReadImports(
            store, files, GoFiles);

        // ten rows from five statements, grouped and single alike, and every spec's module column keeps
        // the quoted path the import resolved while the binding is what the file can type: the last
        // element for a plain path, the alias where the spec writes one
        rows.ShouldBe(
        [
            ("fmt", "fmt", 3),
            ("os", "os", 3),
            ("demo.local/gomini/internal/registry", "registry", 3),
            ("fmt", "fmt", 3),
            ("net/http", "http", 3),
            ("strings", "strings", 3),
            ("demo.local/gomini/internal/text", "text", 3),
            ("strings", "strings", 3),
            ("strings", "strings", 3),
            ("unicode", "unicode", 3),
        ]);

        // go's rule is its own and the mirror of python's: a path binds its LEAF package. No edge
        // anywhere in the pool names a directory hop or the module's repository root
        var goRefs = files.Values
            .Where(f => f.Language == "go")
            .SelectMany(f => store.ListRefsByFile(f.Id))
            .ToList();
        goRefs.Count(r => r.Kind == "import").ShouldBe(rows.Count);
        goRefs.ShouldNotContain(r => r.ToName == "gomini" || r.ToName == "internal" || r.ToName == "demo.local");
    }

    [Fact]
    public void FindReferences_GoStructName_ListsUsesAcrossDeclarationsAndSkipsTheTwin()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        var result = new RefLookup(workspace).Find("Registry", null);

        // the twin declares a Registry as well, and its block is empty: an edge written in a go file
        // only ever binds a go row, so the javascript definition is a same-name answer with no callers
        var go = result.Groups.Single(g => g.Definitions.Any(d => d.File.EndsWith(".go", StringComparison.Ordinal)));
        var twin = result.Groups.Single(g => g.Definitions.Any(d => d.File == "web/store.js"));
        twin.Refs.ShouldBeEmpty();

        go.Definitions.ShouldHaveSingleItem().File.ShouldBe("internal/registry/registry.go");
        go.Refs.Select(r => $"{r.File}:{r.Line}:{r.Kind}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(
            [
                "internal/registry/registry.go:52:inherit",
                "internal/registry/registry.go:55:reference",
            ]);
    }

    [Fact]
    public void FindReferences_GoInterfaceMethod_SharesOneCallListWithTheImplementation()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        var result = new RefLookup(workspace).Find("Lookup", null);

        // the interface element and the method that satisfies it are two declarations of one name, and the
        // only call site names the bare word, so both definitions answer with the same callsite — the
        // fan-out norm 111 shipped for rust and C#, at go fixture scale
        var group = result.Groups.ShouldHaveSingleItem();
        group.Definitions.Select(d => $"{d.File}:{d.StartLine}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["internal/registry/registry.go:50", "internal/registry/store.go:15"]);
        group.Refs.ShouldHaveSingleItem().File.ShouldBe("cmd/report/main.go");

        // and the definition listing is case-insensitive while edge binding is not, so asking for
        // 'normalize' answers the go function too — two blocks, only one of them with callers
        var mixed = new RefLookup(workspace).Find("normalize", null);
        mixed.Groups.Count.ShouldBe(2);
        mixed.Groups.Single(g => g.Refs.Count > 0).Definitions.ShouldHaveSingleItem().File
            .ShouldBe("internal/text/text.go");
        mixed.Groups.Single(g => g.Refs.Count == 0).Definitions.ShouldHaveSingleItem().File
            .ShouldBe("web/store.js");
    }

    [Fact]
    public void Index_GoRefusals_LeaveNoRowAtAll_WhileTheGroupTheySitInKeepsItsOwn()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        // the whole row set of registry.go, and every refusal the grammar makes is a hole in it: the
        // multi-name const spec ('Bad, Also'), the blank at three different kinds ('var _', 'func _()',
        // 'type _ int'), the interface inside an anonymous var ('Do'), and the two body-level bindings
        // ('const local', 'var scratch') that go keeps out while rust ships rows for its own
        store.ListSymbolsByFile(files["internal/registry/registry.go"].Id)
            .Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
            [
                "A",
                "Add",
                "Alias",
                "All",
                "B",
                "Base",
                "Counter",
                "Generic",
                "Get",
                "Len",
                "Limit",
                "Lookup",
                "Lookuper",
                "Map",
                "MaxSize",
                "Mode",
                "Name",
                "New",
                "Registry",
                "Shared",
                "Sink",
                "anonymous",
            ]);

        // the control that keeps the silence a rule: the group the blank sits next to does ship rows, and
        // the named sibling of a refused multi-name spec is not refused for being next to it
        var sink = store.ListSymbolsByFile(files["internal/registry/registry.go"].Id).Single(s => s.Name == "Sink");
        sink.Kind.ShouldBe("field");
        store.ListSymbolsByRepo(repoId).ShouldNotContain(s => s.Name == "_");
        store.ListSymbolsByRepo(repoId).ShouldNotContain(s => s.ContainerName == "Sink");
    }

    [Fact]
    public void Index_GoLocalTypeAndUnnamedReceiver_KeepTheOwnerTheGrammarNames()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var storeRows = store.ListSymbolsByFile(files["internal/registry/store.go"].Id);

        // a type declared in a function body is kept and attributed to that function by its bare name —
        // go methods are package-level, so no receiver is spliced into the chain above a local type
        var window = storeRows.Single(s => s.Name == "window");
        window.Kind.ShouldBe("class");
        window.ContainerName.ShouldBe("Lookup");
        window.StartLine.ShouldBe(19);
        window.EndLine.ShouldBe(21);

        // an unnamed receiver still owns its method: 'func (*Registry) All' has no parameter name to read,
        // so the owner comes from the receiver's type field exactly as a named one does
        storeRows.Single(s => s.Name == "All").ContainerName.ShouldBe("Registry");
        storeRows.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["All", "Item", "Label", "Lookup", "Put", "Store", "Widget", "pick", "window"]);
    }

    [Fact]
    public void Index_GoConstructionAggregateAndQualifiedReadShapes_EmitNoEdge()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var registryRows = store.ListSymbolsByFile(files["internal/registry/registry.go"].Id);
        var registryRefs = store.ListRefsByFile(files["internal/registry/registry.go"].Id);
        registryRefs.ShouldNotBeEmpty();

        // 'Registry{}' and '&Registry{…}' are how go constructs, and construction is not a reference —
        // the same answer 'new Ledger()' gives in javascript, which the rust carrier pins from the other
        // side. The two rows this struct does own are the interface embed and the alias
        var structRow = registryRows.Single(s => s.Name == "Registry" && s.Kind == "class");
        store.ListRefsByFromSymbol(structRow.Id).Select(r => r.Line).OrderBy(l => l).ShouldBe([52, 55]);

        // aggregates name nothing on their own account: '[]string' and 'func(int) error' yield no edge,
        // while the named and qualified fields between them do
        registryRefs.ShouldNotContain(r => r.Line == 40 || r.Line == 45);
        registryRefs.ShouldContain(r => r.Line == 41 && r.ToName == "Text");
        registryRefs.ShouldContain(r => r.Line == 42 && r.ToName == "Handler");

        // and a qualified READ of a package constant is not a call: main.go types 'registry.MaxSize' and
        // the row for MaxSize stays as empty as the composite literal's
        var mainRefs = store.ListRefsByFile(files["cmd/report/main.go"].Id);
        mainRefs.ShouldNotContain(r => r.ToName == "MaxSize");
        registryRows.ShouldContain(s => s.Name == "MaxSize");
        store.ListRefsByFromSymbol(registryRows.Single(s => s.Name == "MaxSize").Id).ShouldBeEmpty();
    }

    [Fact]
    public void CodeSearch_ExactArm_FindsGoChunkByIdentifier()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        var response = new SearchEngine(workspace).Search(
            new SearchQuery("DefaultWidth", false, null, null, null, null, null, null));

        var hit = response.Matches.Single(m => m.Path == "internal/text/text.go");
        hit.Repo.ShouldBe("demo");
        hit.Line.ShouldBe(8);
        hit.Snippet.ShouldContain("DefaultWidth");
    }

    [Fact]
    public async Task CodeSearch_SemanticArm_FindsGoChunkWithoutATermHit()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);
        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var registry = store.ListFiles(repoId).Single(f => f.Path == "internal/registry/registry.go");
        var chunk = store.ListChunksByFile(registry.Id).Single(c => c.StartLine == 59);
        store.Dispose();

        var response = await new SearchEngine(workspace).SearchHybridAsync(new SearchQuery("unmatched paraphrase zzz", false, null, null, null, null, null, null), TestVectorPhase.Arm(workspace, new TsFixtureHarness.FakeEmbeddingProvider(), new TsFixtureHarness.OneHitVectorStore(repoId, chunk)), CancellationToken.None);

        var hit = response.Matches.Single(m => m.Path == "internal/registry/registry.go");
        hit.Source.ShouldBe("semantic");
        hit.Snippet.ShouldContain("func New() *Registry");
    }

    [Fact]
    public void Index_GoAndJsTwin_MatchOnPerSymbolChunkGranularity()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var codeFiles = store.ListFiles(repoId).Where(f => f.Kind == "code").ToList();

        codeFiles.Count(f => f.Language == "go").ShouldBe(4);
        codeFiles.Count(f => f.Language == "javascript").ShouldBe(1);

        foreach (var file in codeFiles)
        {
            var symbols = store.ListSymbolsByFile(file.Id);
            var chunks = store.ListChunksByFile(file.Id);

            // one chunk per symbol, and no orphan chunk, on both sides: the go files carry grouped consts
            // and receivers the javascript twin has no shape for, and the ratio is still the same number
            chunks.Count.ShouldBe(symbols.Count);
            chunks.ShouldAllBe(c => c.SymbolId != null);
            symbols.ShouldAllBe(s => chunks.Any(c => c.StartLine == s.StartLine && c.EndLine == s.EndLine));
        }

        store.ListSymbolsByFile(codeFiles.Single(f => f.Language == "javascript").Id).Count.ShouldBe(3);
    }

    [Fact]
    public void Index_GoEdgeTotals_PinTheUnresolvedNormAlongsideTheResolvedOnes()
    {
        var workspace = TsFixtureHarness.IndexedFixture("GoMini", "web/store.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var goFiles = store.ListFiles(repoId).Where(f => f.Language == "go").ToList();
        goFiles.Count.ShouldBe(4);

        var refs = goFiles.SelectMany(f => store.ListRefsByFile(f.Id)).ToList();
        var byKind = refs.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());

        // a go module imports more than it defines, and the type positions name builtins ('string', 'int')
        // that have no row to bind — the unresolved majority is the norm here, pinned as measured numbers
        refs.Count.ShouldBe(46);
        byKind["import"].ShouldBe(10);
        byKind["call"].ShouldBe(21);
        byKind["inherit"].ShouldBe(2);
        byKind["reference"].ShouldBe(13);
        refs.Count(r => r.FromSymbolId == 0).ShouldBe(32);

        // and the resolved third binds go rows only, never the twin that shares 'Registry' and 'add' with
        // this module in the same repo
        var languageBySymbol = store.ListSymbolsByRepo(repoId)
            .ToDictionary(s => s.Id, s => store.FindFileById(s.FileId)!.Language);
        refs.Where(r => r.FromSymbolId != 0)
            .ShouldAllBe(r => languageBySymbol[r.FromSymbolId] == "go");
    }
}
