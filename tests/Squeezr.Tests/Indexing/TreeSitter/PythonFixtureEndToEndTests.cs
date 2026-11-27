using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing.TreeSitter;

// PyMini through the whole pipeline: package plus subpackage, indexed as a repo that also holds a
// javascript twin, so the pool wall and the chunk granularity are both read from the same store the
// tools answer from. Every count below was produced by indexing the fixture (the dump lives next to the
// review carrier) rather than computed by hand, because the store reads are the product's own output.
//
// Nothing here asserts doc-comment capture. The fixture writes a module and a class docstring on
// purpose — that is what a python package looks like — and the deferral covers three
// mechanisms (rust ///, python docstring, go // renderer-only) and stays open.
public class PythonFixtureEndToEndTests
{
    private const string JsTwinSource = """
        export class Registry {
          static MAX_ITEMS = 8;

          constructor(rows) {
            this.rows = rows;
          }

          add(row) {
            return row;
          }
        }

        export function build(rows) {
          return rows.length;
        }

        export const VERSION = "0.1.0";
        """;

    private static readonly string[] PythonFiles =
    [
        "mini/__init__.py",
        "mini/engine.py",
        "mini/registry.py",
        "mini/utils/__init__.py",
        "mini/utils/text.py",
    ];

    [Fact]
    public void Index_PyMiniPackage_PersistsEveryFileAsPythonCodeWithPerSymbolChunks()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        // the packaging marker is data, not a symbol source — the same answer Cargo.toml gives, and the
        // reason the marker can sit in the fixture at all
        files["pyproject.toml"].Kind.ShouldBe("docs");
        files["pyproject.toml"].Language.ShouldBe("unknown");
        PythonFiles.ShouldAllBe(path => files[path].Language == "python" && files[path].Kind == "code");

        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in PythonFiles)
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

            // embed side measured on persisted rows: the fake provider plus the dead endpoint mean no
            // vector left the machine, while every chunk still has to leave the run stamped as embedded
            fileChunks.ShouldAllBe(c => c.VectorStatus == "ok" && c.ModelKey != null);
        }

        symbols.ShouldBe(new Dictionary<string, int>
        {
            ["mini/__init__.py"] = 3,
            ["mini/engine.py"] = 14,
            ["mini/registry.py"] = 3,
            ["mini/utils/__init__.py"] = 1,
            ["mini/utils/text.py"] = 6,
        });
    }

    [Fact]
    public void SymbolLookup_DecoratedFunction_AnswersFromTheDefLineInsideTheDecoratorSpan()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        var result = new SymbolLookup(workspace).Find("build", null, null, 400, body: false);

        // 'build' is a name the fixture and its javascript twin both own, and the lookup answers two rows
        // instead of choosing one — the same-name pair is the wall's normal output, not a collision to fix
        result.Matches.Count.ShouldBe(2);
        var python = result.Matches.Single(m => m.File == "mini/engine.py");

        // the decorator is a span question, not a signature question: the row starts at '@app.route' so
        // the chunk covers the decorator line, while the signature stays the line an agent greps for
        python.Repo.ShouldBe("demo");
        python.Kind.ShouldBe("function");
        python.ContainerName.ShouldBeNull();
        python.Signature.ShouldBe("def build(rows):");
        python.StartLine.ShouldBe(49);
        python.EndLine.ShouldBe(55);
        // the twin row is measured, not assumed: an exported function signs itself from its own node, so
        // the 'export' keyword is not part of it
        result.Matches.Single(m => m.File == "web/registry.js").Signature.ShouldBe("function build(rows)");
    }

    [Fact]
    public void SymbolLookup_PythonMethod_NamesItsClassContainerInTheSameRowThePackComposes()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        var hit = new SymbolLookup(workspace)
            .Find("empty", null, null, 400, body: false)
            .Matches.ShouldHaveSingleItem();

        hit.Repo.ShouldBe("demo");
        hit.File.ShouldBe("mini/engine.py");
        hit.Kind.ShouldBe("method");
        hit.Signature.ShouldBe("def empty():");

        // the container column is what lets a reader pair 'Registry.empty' the way the pack composer
        // spells it, and a @staticmethod-decorated def keeps the decorator line inside its span
        hit.ContainerName.ShouldBe("Registry");
        hit.StartLine.ShouldBe(35);
        hit.EndLine.ShouldBe(37);

        // members are a body-omission remedy and not an always-on column — SymbolMembersTests owns that
        // budget shape, so this row only pins that reading a definition without its body asks for none
        hit.Members.ShouldBeEmpty();
    }

    [Fact]
    public void FindReferences_PythonClass_BindsImportsBasesAndCallsAndNeverTheJsTwin()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        var result = new RefLookup(workspace).Find("Registry", null);

        // the twin declares a Registry too, and it sits in the same repo — what keeps its block empty is
        // the pool: python edges only ever bind python rows, so the javascript definition shows as a
        // same-name declaration nobody in this repo references
        var python = result.Groups.Single(g => g.Definitions.Any(d => d.File == "mini/engine.py"));
        var twin = result.Groups.Single(g => g.Definitions.Any(d => d.File == "web/registry.js"));
        twin.Refs.ShouldBeEmpty();
        twin.Definitions.ShouldHaveSingleItem().Kind.ShouldBe("class");

        python.Definitions.ShouldHaveSingleItem().File.ShouldBe("mini/engine.py");
        python.Refs.Select(r => $"{r.File}:{r.Line}:{r.Kind}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(
            [
                "mini/__init__.py:3:import",
                "mini/__init__.py:8:call",
                "mini/engine.py:37:call",
                "mini/registry.py:3:import",
                "mini/registry.py:8:inherit",
            ]);
        python.Refs.Single(r => r.File == "mini/engine.py").EnclosingSymbol.ShouldBe("Registry.empty");
    }

    [Fact]
    public void FindReferences_SameNamePythonFunctions_ShareOneCallListAcrossModules()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        var result = new RefLookup(workspace).Find("render", null);

        // engine.py and utils/text.py each own a 'render', and the only call site names the bare word, so
        // one callsite answers for both declarations — the fan-out shape find_references already ships
        // for rust and C#, pinned for python at fixture scale
        var group = result.Groups.ShouldHaveSingleItem();
        group.Definitions.Select(d => $"{d.File}:{d.StartLine}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["mini/engine.py:58", "mini/utils/text.py:17"]);
        group.Refs.ShouldHaveSingleItem().File.ShouldBe("mini/engine.py");
        group.Refs.ShouldHaveSingleItem().Line.ShouldBe(40);
    }

    [Fact]
    public void Index_DottedImport_BindsTheTopPackage_WhileTheLeafSymbolElsewhereGetsNothing()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var importer = files["mini/registry.py"];
        var decoyFile = files["mini/engine.py"];
        var pythonRefs = files.Values
            .Where(f => f.Language == "python")
            .SelectMany(f => store.ListRefsByFile(f.Id))
            .ToList();
        pythonRefs.ShouldNotBeEmpty();

        // review-023-03 BLOCKER-1 at e2e scale. 'import mini.utils.text' gives the file the name 'mini'
        // and keeps the whole path in the module column; the leaf segment is not a name the file can type
        var dotted = store.ListRefsByFile(importer.Id).Where(r => r.Line == 1).ToList();
        dotted.ShouldHaveSingleItem();
        dotted[0].Kind.ShouldBe("import");
        dotted[0].ToName.ShouldBe("mini");
        dotted[0].ToSymbolId.ShouldBe("mini.utils.text");

        // the decoy: engine.py really does declare a function called 'text', and no edge anywhere in the
        // python pool points at it — not the dotted import above, not a call, nothing
        var decoy = store.ListSymbolsByFile(decoyFile.Id).Single(s => s.Name == "text");
        store.ListRefsByFromSymbol(decoy.Id).ShouldBeEmpty();
        pythonRefs.ShouldNotContain(r => r.ToName == "text");

        // the positive control that keeps the refusal a rule and not a dead walk: the from-import of the
        // same leaf module binds the name the file can type, and it resolves to the row in that module
        store.ListRefsByFile(importer.Id).Single(r => r.Line == 4 && r.Kind == "import")
            .FromSymbolId.ShouldBe(store.ListSymbolsByFile(files["mini/utils/text.py"].Id)
                .Single(s => s.Name == "normalize").Id);
    }

    [Fact]
    public void Index_PythonImportShapes_PinTheBindingAndTheModuleColumnOfEveryForm()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var rows = TsFixtureHarness.ReadImports(
            store, files, "mini/registry.py", "mini/utils/__init__.py");

        // six statement forms in two files, each with its own binding rule: plain dotted (top package),
        // aliased dotted (the alias, and the module column loses the leaf), from-import (last segment),
        // relative (module column kept verbatim, the way javascript stores './util')
        rows.ShouldBe(
        [
            ("mini.utils.text", "mini", 1),
            ("mini", "core", 2),
            ("mini.engine", "Registry", 3),
            ("mini.engine", "build", 3),
            ("mini.utils.text", "normalize", 4),
            (".", "session", 5),
            (".text", "normalize", 1),
        ]);
    }

    [Fact]
    public void Index_DeclaredPythonShapes_EmitNoEdgeOrNoRowWhereTheGrammarSaysSo()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var engine = files["mini/engine.py"];
        var registry = files["mini/registry.py"];

        var engineSymbols = store.ListSymbolsByFile(engine.Id);
        var engineRefs = store.ListRefsByFile(engine.Id);
        engineSymbols.ShouldNotBeEmpty();
        engineRefs.ShouldNotBeEmpty();

        // a metaclass keyword is configuration, not inheritance: the base list yields one edge and the
        // name it never binds is exactly the one written after 'metaclass='
        engineRefs.Where(r => r.Line == 24).Select(r => (r.Kind, r.ToName)).ShouldBe([("inherit", "Shape")]);
        engineSymbols.ShouldNotContain(s => s.Name == "RegistryMeta");
        engineRefs.ShouldNotContain(r => r.ToName == "RegistryMeta");

        // a def body writes a lot of statements that are not declarations, and the whole row set of the
        // file is the honest way to say it: fourteen names, none of them a lowercase binding, an annotated
        // assignment, a tuple target or a write through an attribute or a subscript
        engineSymbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
        [
            "BASE",
            "GLOBAL_ROWS",
            "MAX_ITEMS",
            "Registry",
            "Shape",
            "add",
            "build",
            "drain",
            "empty",
            "inner",
            "label",
            "render",
            "sides",
            "text",
        ]);

        // a callee that is a subscript names nothing, and a function used as a value is not a call:
        // neither line yields a row, while the two calls on the lines below it do
        engineRefs.ShouldNotContain(r => r.Line == 51 || r.Line == 53);
        engineRefs.ShouldContain(r => r.Line == 54 && r.Kind == "call" && r.ToName == "len");

        // a decorator that CALLS something is a call like any other, and a dotted one names its member:
        // '@authenticated("registry")' at 23 signs 'authenticated', '@app.route("/build")' at 49 signs
        // 'route' and never 'app' — the same judgement the dotted callee rule gives in ordinary code
        engineRefs.ShouldContain(r => r.Line == 23 && r.Kind == "call" && r.ToName == "authenticated");
        engineRefs.ShouldContain(r => r.Line == 49 && r.Kind == "call" && r.ToName == "route");
        engineRefs.ShouldNotContain(r => r.ToName == "app" && r.Kind == "call");

        // and a decorator that only names something is annotation, not invocation: bare '@dataclass' at 22
        // signs no edge at all
        engineRefs.ShouldNotContain(r => r.Line == 22);

        // an attribute read is not a call: 'core.rows' sits in open_snapshot and produces no edge, so the
        // only rows the function body yields are the three it really invokes
        var openSnapshot = store.ListRefsByFile(registry.Id)
            .Where(r => r.EnclosingSymbol == "open_snapshot")
            .Select(r => $"{r.Line}:{r.ToName}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        openSnapshot.ShouldBe(["14:Snapshot", "15:load", "17:capture"]);

        // the enclosing column keeps the innermost two segments, so a doubly-nested def reads
        // 'label.inner' while its own row carries the full chain 'Registry.label' — pre-existing for
        // every pooled language and pinned because a reader that pairs the two would miss this row
        engineRefs.ShouldContain(r => r.Line == 44 && r.EnclosingSymbol == "label.inner");
        engineSymbols.Single(s => s.Name == "inner").ContainerName.ShouldBe("Registry.label");
    }

    [Fact]
    public void CodeSearch_ExactArm_FindsPythonChunkByIdentifier()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        var response = new SearchEngine(workspace).Search(
            new SearchQuery("MAX_LINES", false, null, null, null, null, null, null));

        var hit = response.Matches.Single(m => m.Path == "mini/utils/text.py");
        hit.Repo.ShouldBe("demo");
        hit.Line.ShouldBe(7);
        hit.Snippet.ShouldContain("MAX_LINES");
    }

    [Fact]
    public async Task CodeSearch_SemanticArm_FindsPythonChunkWithoutATermHit()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);
        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var engine = store.ListFiles(repoId).Single(f => f.Path == "mini/engine.py");
        var chunk = store.ListChunksByFile(engine.Id).Single(c => c.StartLine == 39);
        store.Dispose();

        var response = await new SearchEngine(workspace).SearchHybridAsync(new SearchQuery("unmatched paraphrase zzz", false, null, null, null, null, null, null), TestVectorPhase.Arm(workspace, new TsFixtureHarness.FakeEmbeddingProvider(), new TsFixtureHarness.OneHitVectorStore(repoId, chunk)), CancellationToken.None);

        var hit = response.Matches.Single(m => m.Path == "mini/engine.py");
        hit.Source.ShouldBe("semantic");
        hit.Snippet.ShouldContain("async def drain");
    }

    [Fact]
    public void Index_PythonAndJsTwin_MatchOnPerSymbolChunkGranularity()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var codeFiles = store.ListFiles(repoId).Where(f => f.Kind == "code").ToList();

        // both grammars are in the denominator: five python modules plus the javascript twin
        codeFiles.Count(f => f.Language == "python").ShouldBe(5);
        codeFiles.Count(f => f.Language == "javascript").ShouldBe(1);

        foreach (var file in codeFiles)
        {
            var symbols = store.ListSymbolsByFile(file.Id);
            var chunks = store.ListChunksByFile(file.Id);

            // the ratio is the claim, not a per-language chunk count: a routed file — python or
            // javascript — ships exactly one chunk per symbol, while the python side carries decorators
            // and docstring lines the javascript twin has no shape for
            chunks.Count.ShouldBe(symbols.Count);
            chunks.ShouldAllBe(c => c.SymbolId != null);
            symbols.ShouldAllBe(s => chunks.Any(c => c.StartLine == s.StartLine && c.EndLine == s.EndLine));
        }

        var twin = codeFiles.Single(f => f.Language == "javascript");
        store.ListSymbolsByFile(twin.Id).Count.ShouldBe(4);
    }

    [Fact]
    public void Index_PythonEdgeTotals_PinTheUnresolvedNormAlongsideTheResolvedOnes()
    {
        var workspace = TsFixtureHarness.IndexedFixture("PyMini", "web/registry.js", JsTwinSource);

        using var store = TsFixtureHarness.OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        var pythonFiles = store.ListFiles(repoId).Where(f => f.Language == "python").ToList();
        pythonFiles.Count.ShouldBe(5);

        var refs = pythonFiles.SelectMany(f => store.ListRefsByFile(f.Id)).ToList();
        var byKind = refs.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());

        // a python package imports the world and defines a fraction of it, so 'every edge resolves' is a
        // false expectation: os/json/sys/typing rows are kept unresolved, which is the norm this fixture
        // inherits from rust's std rows. The counts are the measured ones, not a hope
        refs.Count.ShouldBe(43);
        byKind["import"].ShouldBe(20);
        byKind["call"].ShouldBe(21);
        byKind["inherit"].ShouldBe(2);
        byKind.ShouldNotContainKey("reference");
        refs.Count(r => r.FromSymbolId == 0).ShouldBe(25);

        // and the resolved half of them points only at python rows: a name that also exists in the
        // javascript twin of the same repo never binds across the wall
        var languageBySymbol = store.ListSymbolsByRepo(repoId)
            .ToDictionary(s => s.Id, s => store.FindFileById(s.FileId)!.Language);
        refs.Where(r => r.FromSymbolId != 0)
            .ShouldAllBe(r => languageBySymbol[r.FromSymbolId] == "python");
    }
}
