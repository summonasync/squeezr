using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Fact tests for python references, the mirror of TreeSitterRustReferenceTests. Every expected row was
// read off the extractor (reference-shape probe) before the test was written, in the order the walker
// emits it — pre-order, so a chained call's outer row comes before the inner one
public class TreeSitterPythonReferenceTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string FixtureSource = """
        import os
        import os, sys
        import a.b.c as abc
        from typing import Dict, List as L
        from os.path import join
        from . import sibling
        from ..pkg import thing
        from typing import *

        class Bare:
            pass

        @dataclass
        @app.route("/x")
        class Widget(Base, Trusted, mixin.Mixin, Generic[T], metaclass=ABCMeta):
            registry: Dict[str, int] = {}

            def render(self, items):
                funcs[0](1)
                joined = os.path.join("a", "b").upper()
                return helper(sum(items), Base(), items.map(lambda x: inner(x)))
        """;

    private static readonly SymbolRef[] FixtureImports =
    [
        new(null, "os", RefKind.Import, 1),
        new(null, "os", RefKind.Import, 2),
        new(null, "sys", RefKind.Import, 2),
        new("a.b", "abc", RefKind.Import, 3),
        new("typing", "Dict", RefKind.Import, 4),
        new("typing", "L", RefKind.Import, 4),
        new("os.path", "join", RefKind.Import, 5),
        new(".", "sibling", RefKind.Import, 6),
        new("..pkg", "thing", RefKind.Import, 7),
        new("typing", "typing", RefKind.Import, 8),
    ];

    [Fact]
    public void Python_ImportTree_EmitsOneRowPerBindingOfEveryShape()
    {
        var imports = Extract(FixtureSource).Where(r => r.Kind == RefKind.Import).ToArray();

        // ten rows from eight statements: 'import os, sys' contributes both names because the name field
        // repeats in python and the reader walks children, not fields; an alias binds under the alias and
        // keeps the dotted prefix as its module; every 'from x import …' keeps x in the module column,
        // including the raw dot text of a relative import, and 'from typing import *' — which names no
        // binding at all — binds the module it stars
        imports.Length.ShouldBe(FixtureImports.Length);
        imports.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
            FixtureImports.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)));
        imports.ShouldAllBe(r => r.EnclosingName == null);
    }

    [Fact]
    public void Python_ChainedAndNestedCalls_NameTheMemberAndEachCalleeGetsItsOwnRow()
    {
        var calls = Extract(FixtureSource).Where(r => r.Kind == RefKind.Call).ToArray();

        // the chained line emits 'upper' before 'join' (the outer call is walked first), and the return
        // line emits every callee in its arguments — a call inside an argument list is still a call
        calls.Select(r => (r.ToName, r.Line)).ShouldBe(
        [
            ("route", 14),
            ("upper", 20),
            ("join", 20),
            ("helper", 21),
            ("sum", 21),
            ("Base", 21),
            ("map", 21),
            ("inner", 21),
        ]);

        // javascript reaches the same judgement on member_expression.property, rust on
        // field_expression.field: a dotted callee is named after its member, never after the chain. So
        // 'os.path' contributes no row here while 'join' does — three grammars, three node-kind names,
        // one rule
        calls.Select(r => r.ToName).ShouldNotContain("os.path");
        calls.Select(r => r.ToName).ShouldNotContain("os");

        // the decorator difference: '@app.route("/x")' holds a real call node and emits 'route', while the
        // bare '@dataclass' above it holds only an identifier and emits nothing
        calls.ShouldNotContain(r => r.ToName == "dataclass");
    }

    [Fact]
    public void Python_SubscriptCallee_EmitsNoEdge_WhileTheSameBodyEmitsItsNeighbours()
    {
        var names = Extract(FixtureSource).Select(r => r.ToName).ToList();

        // 'funcs[0](1)' has a callee no rule can name (field 'function' is a subscript), so it is a silent
        // statement — pinned against the calls on the lines around it, which do arrive, so the absence is
        // the shape and not a walk that skipped the body
        names.ShouldNotContain("funcs");
        names.ShouldContain("join");
        names.ShouldContain("upper");
        names.ShouldContain("inner");
    }

    [Fact]
    public void Python_ClassBases_EmitInheritRows_AndKeywordArgumentsDoNot()
    {
        var inherits = Extract(FixtureSource).Where(r => r.Kind == RefKind.Inherit).ToArray();

        // four bases from one class line: a plain name, a second name, a dotted one (named after its
        // member like any dotted callee) and a parameterised one (named after the type, not the brackets).
        // 'metaclass=ABCMeta' sits in the same argument_list as a keyword_argument and is configuration,
        // not a parent — an edge here would read as inheritance in symbol_lookup
        inherits.Select(r => r.ToName).ShouldBe(["Base", "Trusted", "Mixin", "Generic"]);
        inherits.ShouldAllBe(r => r.Line == 15);
        inherits.ShouldAllBe(r => r.ToSymbolId == null);
        inherits.ShouldAllBe(r => r.EnclosingName == null);

        // 'class Bare:' carries no superclasses field at all, so these four rows are the only Inherit rows
        // the file can produce — the bare class contributes nothing and is still a symbol row
        Extract(FixtureSource).Count(r => r.Kind == RefKind.Inherit).ShouldBe(4);
        Extract(FixtureSource).ShouldNotContain(r => r.ToName == "ABCMeta");

        // the pure form of the same judgement: a class whose only parenthesised argument is a keyword
        // inherits nothing at all — and the row count here is the denominator, so an empty result cannot
        // come from a walk that never reached the class
        var keywordOnly = Parse("src/meta.py", "class Made:\n    metaclass_only = 1\n").Symbols;
        keywordOnly.ShouldNotBeEmpty();
        var withKeywordOnlyBase = Extract("class Made(metaclass=ABCMeta):\n    pass\n").ToList();
        withKeywordOnlyBase.ShouldBe([]);

        // and a keyword alongside a real base leaves exactly the base
        Extract("class Made(Base, metaclass=ABCMeta):\n    pass\n").Select(r => (r.ToName, r.Kind)).ShouldBe(
        [("Base", RefKind.Inherit)]);
    }

    [Fact]
    public void Python_EnclosingNameIsTheSymbolContainerChainByteForByte()
    {
        var result = Parse("src/widget.py", FixtureSource);
        var render = result.Symbols.Single(s => s.Name == "render");

        // the reference's enclosing column and the symbol's own identity have to agree, or member-scoped
        // answers drift apart: 'render' is a Method whose container is 'Widget', and every call in its body
        // reports 'Widget.render' — two innermost segments, exactly as the shipped js and rust shapes do
        render.Kind.ShouldBe(SymbolKind.Method);
        render.ContainerName.ShouldBe("Widget");
        var calls = result.Refs.Where(r => r.Kind == RefKind.Call && r.Line == 21).ToList();
        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(r => r.EnclosingName == $"{render.ContainerName}.{render.Name}");

        // and a call written in a CLASS body — a constant built from a stdlib call — encloses on the class,
        // because the nearest declared container is the class and no method is involved
        var classBody = Parse("src/config.py", "class Config:\n    DEBUG = os.environ.get(\"D\", 0)\n").Refs;
        classBody.ShouldHaveSingleItem().ToName.ShouldBe("get");
        classBody[0].EnclosingName.ShouldBe("Config");
    }

    [Fact]
    public void Python_TypeAnnotations_EmitNoEdgesYet()
    {
        var refs = Extract("registry: Dict[str, int] = {}\ncount = len(registry)\n").ToList();

        // 'Dict[str, int]' in an annotation position is a type node, and python's type-target gate is
        // class_definition only, so no edge is invented for a hint. The control is the same file's call,
        // which does arrive: the silence is the rule, not a walk that never got here. Capturing
        // annotations is a separate decision with its own table row, not a freebie from this one
        refs.ShouldNotContain(r => r.ToName == "Dict");
        refs.ShouldHaveSingleItem().ToName.ShouldBe("len");
    }

    [Fact]
    public void Python_SuperCall_EmitsTheMemberAndTheSuperExpression()
    {
        var refs = Extract("""
            class Sub(Base):
                def hook(self):
                    return super().hook()
            """).ToList();

        // 'super().hook()' is two calls in python's grammar: the outer member call and the inner
        // no-argument super — both are kept, and the unresolved std name stays a row the same way rust
        // keeps its own, rather than being dropped because nothing in the pool answers it
        refs.Select(r => (r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("Base", RefKind.Inherit, 1),
            ("hook", RefKind.Call, 3),
            ("super", RefKind.Call, 3),
        ]);
        refs[1].EnclosingName.ShouldBe("Sub.hook");
        refs[2].EnclosingName.ShouldBe("Sub.hook");
    }

    [Fact]
    public void PythonReferenceRules_DoNotHarvestJavascriptTrees_WhereTheImportNodeKindIsATwin()
    {
        using var jsGrammar = new Language("JavaScript");
        using var jsParser = new Parser(jsGrammar);
        using var jsTree = jsParser.Parse("""
            import { mul } from './util';

            export function run(items) {
              return mul(items.length, 2);
            }
            """)!;

        TsReferenceExtractor.Extract(jsTree.RootNode!, "python", "src/run.js").ShouldBe([]);

        // the control that makes the silence mean 'the rules are python-shaped' rather than 'the walker did
        // not run': the same tree through its own label emits the import binding and the call
        var own = TsReferenceExtractor.Extract(jsTree.RootNode!, "javascript", "src/run.js");
        own.Select(r => (r.ToSymbolId, r.ToName, r.Kind)).ShouldBe([("./util", "mul", RefKind.Import), (null, "mul", RefKind.Call)]);

        using var pyGrammar = new Language("Python");
        using var pyParser = new Parser(pyGrammar);
        using var pyTree = pyParser.Parse(FixtureSource)!;

        // and the wall holds in the other direction — javascript's reader finds no import_clause-shaped
        // bindings to harvest from a python file, whatever node kinds the two grammars share
        TsReferenceExtractor.Extract(pyTree.RootNode!, "javascript", "src/widget.py").ShouldBe([]);
        TsReferenceExtractor.Extract(pyTree.RootNode!, "python", "src/widget.py").Count.ShouldBe(22);
    }

    [Fact]
    public void Index_PythonEdgesResolveInsidePython_AndStdNamesStayUnresolvedRows()
    {
        var workspace = NewPythonWorkspace();
        WriteFile(workspace, "repo/src/mymod.py", "def helper(items):\n    return len(items)\n");
        WriteFile(workspace, "repo/src/user.py", "def use(items):\n    return helper(items)\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var defFile = store.ListFiles(repoId).Single(f => f.Path == "src/mymod.py");
        var useFile = store.ListFiles(repoId).Single(f => f.Path == "src/user.py");

        var helper = store.ListSymbolsByFile(defFile.Id).Single(s => s.Name == "helper");

        // two python files are one pool, so a call in another module binds — resolution is by name inside
        // the pool, and the module column is informational rather than a join key
        var edge = store.ListRefsByFile(useFile.Id).Single(r => r.ToName == "helper");
        edge.Kind.ShouldBe("call");
        edge.FromSymbolId.ShouldBe(helper.Id);

        // 'len' is a stdlib name with no python symbol row anywhere: the edge is kept and stays
        // unresolved, exactly the shape rust's std rows shipped with — no edge is invented and none is
        // dropped
        var std = store.ListRefsByFile(defFile.Id).Single(r => r.ToName == "len");
        std.FromSymbolId.ShouldBe(0);
    }

    [Fact]
    public void Index_PythonImportEdgeLivesOnALineNoSymbolCovers()
    {
        var workspace = NewPythonWorkspace();
        WriteFile(workspace, "repo/src/loader.py", "import os\n\ndef load(path):\n    return os.listdir(path)\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var file = store.ListFiles(repoId).Single(f => f.Path == "src/loader.py");

        // the ruling this row pins: a module-level import is an edge whose own line belongs to no symbol
        // span, the same shape rust's 'use std::…' has been shipping since 111 — the edge exists, the
        // statement is not a definition, and no chunk is expected to cover it. Deliberately no
        // chunk-coverage claim here: the import line is outside every symbol span by design (T2's
        // decorator-span reasoning runs the other way, because a decorator documents the item below it)
        var symbols = store.ListSymbolsByFile(file.Id);
        // the denominator first: 'every symbol starts below line 1' is also true of an empty list, and this
        // file does produce a symbol row — without that, the span claim below measures nothing
        symbols.ShouldNotBeEmpty();
        symbols.Count(s => s.StartLine > 1).ShouldBe(symbols.Count);

        var importEdge = store.ListRefsByFile(file.Id).Single(r => r.Kind == "import");
        importEdge.ToName.ShouldBe("os");
        importEdge.Line.ShouldBe(1);
        importEdge.ToSymbolId.ShouldBeNull();

        // the call on the imported name does land inside the function's span, so the file's edges are not
        // all floating outside coverage
        store.ListRefsByFile(file.Id).Single(r => r.Kind == "call").ToName.ShouldBe("listdir");
    }

    [Fact]
    public void Python_PlainDottedImport_BindsTheTopPackage_AndKeepsTheWholePathAsItsModule()
    {
        // the un-aliased form of the rule: 'import os.path' gives the file the name 'os', never 'path' —
        // python binds the top package and reaches the deeper module through it. The earlier draft of this
        // row read the leaf (review-023-03 BLOCKER-1, measured as a persisted false-positive through
        // find_references), so the whole path now sits in the module column and the binding is the first
        // segment. Without this pin the first-segment rule is only reachable through the alias branch
        var refs = Extract("import os.path\nimport a.b.c\n").ToList();

        refs.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("os.path", "os", RefKind.Import, 1),
            ("a.b.c", "a", RefKind.Import, 2),
        ]);
    }

    [Fact]
    public void Index_PythonDottedImport_DoesNotBindTheLeafOfThePath()
    {
        var workspace = NewPythonWorkspace();
        WriteFile(workspace, "repo/src/paths.py", "def path(item):\n    return item\n");
        WriteFile(workspace, "repo/src/caller.py", "import os.path\n\ndef use():\n    return os.path.split('/')[0]\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var pathsFile = store.ListFiles(repoId).Single(f => f.Path == "src/paths.py");
        var callerFile = store.ListFiles(repoId).Single(f => f.Path == "src/caller.py");

        // the false positive, pinned away: a python module that defines a function called 'path' must not
        // become the target of another file's 'import os.path' — the importer never has the bare name 'path'
        var leaf = store.ListSymbolsByFile(pathsFile.Id).Single(s => s.Name == "path");
        var edges = store.ListRefsByFile(callerFile.Id);
        edges.ShouldNotContain(r => r.Kind == "import" && r.ToName == "path");
        edges.ShouldNotContain(r => r.FromSymbolId == leaf.Id);

        // and the edges the file really produces: the package name from the import, and the member call
        // inside the function — 'os.path' is a dotted expression, not a call, so no row names 'path'
        edges.Single(r => r.Kind == "import").ToName.ShouldBe("os");
        edges.Where(r => r.Kind == "call").Select(r => r.ToName).ShouldBe(["split"]);
    }

    [Fact]
    public void Python_ImportDepthNeverChangesWhatAGlobBinds()
    {
        // the rust glob lesson, mirrored: a wildcard binds the module it stars, and the DEPTH of that
        // module must not decide whether the import resolves — 'from typing import *' and
        // 'from app.services import *' both bind their module's last segment while the module column keeps
        // the difference ('typing' versus 'app.services'). Without the last-segment rule a dotted glob
        // would bind the whole path as a name and silently stop matching
        var refs = Extract("from typing import *\nfrom app.services import *\n").ToList();

        refs.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("typing", "typing", RefKind.Import, 1),
            ("app.services", "services", RefKind.Import, 2),
        ]);
    }

    [Fact]
    public void Python_AliasedBindings_WinOverThePathTheyAlias()
    {
        // an alias replaces the last segment in both import shapes: 'import a.b.c as abc' binds abc from
        // module a.b, and 'from typing import List as L' binds L from typing. A reader that kept the path's
        // own last segment would answer 'c' and 'List' — names the file cannot use at all
        var refs = Extract("import a.b.c as abc\nfrom typing import List as L\n").ToList();

        refs.Select(r => (r.ToSymbolId, r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("a.b", "abc", RefKind.Import, 1),
            ("typing", "L", RefKind.Import, 2),
        ]);
    }

    private static IReadOnlyList<SymbolRef> Extract(string source) =>
        Extract("src/widget.py", source);

    private static IReadOnlyList<SymbolRef> Extract(string path, string source)
    {
        using var grammar = new Language("Python");
        using var parser = new Parser(grammar);
        using var tree = parser.Parse(source)!;
        return TsReferenceExtractor.Extract(tree.RootNode!, "python", path);
    }

    private static ParseResult Parse(string path, string text) =>
        new TreeSitterProvider(Config).Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(text));

    private static string NewPythonWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
