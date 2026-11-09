using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.TreeSitter;

public class TreeSitterRoutingTests
{
    [Fact]
    public void Index_PolyglotWorkspace_JsTsSymbolsChunksRefsAndMdFallback()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoId = store.FindRepo("demo")!.Id;

            var jsFile = store.ListFiles(repoId).Single(f => f.Path == "src/app.js");
            var tsFile = store.ListFiles(repoId).Single(f => f.Path == "src/store.ts");
            var mdFile = store.ListFiles(repoId).Single(f => f.Path == "docs/notes.md");
            var csFile = store.ListFiles(repoId).Single(f => f.Path == "src/a.cs");

            var jsSymbols = store.ListSymbolsByFile(jsFile.Id);
            jsSymbols.Select(s => (s.Name, s.Kind)).ShouldContain(("add", "function"));
            jsSymbols.ShouldContain(s => s.Name == "Calc" && s.Kind == "class");

            var tsSymbols = store.ListSymbolsByFile(tsFile.Id);
            tsSymbols.ShouldContain(s => s.Name == "User" && s.Kind == "interface");
            tsSymbols.ShouldContain(s => s.Name == "find" && s.ContainerName == "Store");

            store.ListSymbolsByFile(csFile.Id).ShouldNotBeEmpty();

            store.ListChunksByFile(jsFile.Id).ShouldAllBe(c => c.SymbolId != null);
            store.ListChunksByFile(tsFile.Id).ShouldAllBe(c => c.SymbolId != null);
            var mdChunks = store.ListChunksByFile(mdFile.Id);
            mdChunks.ShouldNotBeEmpty();
            mdChunks.ShouldAllBe(c => c.SymbolId == null);

            var refs = store.ListRefsByFile(jsFile.Id);
            var importRef = refs.Single(r => r.Kind == "import");
            importRef.ToName.ShouldBe("mul");
            importRef.ToSymbolId.ShouldBe("./util");
            importRef.Line.ShouldBe(1);
            var callRef = refs.Single(r => r.Kind == "call");
            callRef.ToName.ShouldBe("mul");
            callRef.Line.ShouldBe(9);
            callRef.EnclosingSymbol.ShouldBe("Calc.double");

            var utilFile = store.ListFiles(repoId).Single(f => f.Path == "src/util.js");
            var mulSymbol = store.ListSymbolsByFile(utilFile.Id).ShouldHaveSingleItem();
            importRef.FromSymbolId.ShouldBe(mulSymbol.Id);
            callRef.FromSymbolId.ShouldBe(mulSymbol.Id);
        }

        new WorkspaceIndexer().Index(
            workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoId = store.FindRepo("demo")!.Id;
            var allChunks = store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
            allChunks.ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == "ollama/nomic-embed-text/768/nomic-embed-text-v1.5");
        }
    }

    [Fact]
    public void IndexFile_JsEdit_RefreshesSymbolsAndChunks()
    {
        var workspace = NewPolyglotWorkspace();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        WriteFile(workspace, "repo/src/app.js", "export function addTwo(a, b) {\n  return a + b;\n}\n");
        indexer.IndexFile(workspace, "demo", "src/app.js");

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var jsFile = store.ListFiles(repoId).Single(f => f.Path == "src/app.js");
        var jsSymbols = store.ListSymbolsByFile(jsFile.Id);
        jsSymbols.Select(s => s.Name).ShouldBe(["addTwo"]);
        jsSymbols.Select(s => s.Name).ShouldNotContain("add");
        store.ListChunksByFile(jsFile.Id).ShouldAllBe(c => c.Text.Contains("addTwo"));
    }

    [Fact]
    public void Index_RustSymbolNamedLikeJsCallee_KeepsJsEdgesOnTheJsRow()
    {
        var workspace = NewPolyglotWorkspace();
        WriteFile(workspace, "repo/src/mul.rs", "pub fn mul(a: u32, b: u32) -> u32 {\n    a * b\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var rustFile = store.ListFiles(repoId).Single(f => f.Path == "src/mul.rs");
        var utilFile = store.ListFiles(repoId).Single(f => f.Path == "src/util.js");
        var appFile = store.ListFiles(repoId).Single(f => f.Path == "src/app.js");

        // the rust row has to exist, otherwise the pool filter below is asserted against an empty set
        var rustMul = store.ListSymbolsByFile(rustFile.Id).ShouldHaveSingleItem();
        rustMul.Name.ShouldBe("mul");
        var jsMul = store.ListSymbolsByFile(utilFile.Id).ShouldHaveSingleItem();
        jsMul.Name.ShouldBe("mul");

        var edges = store.ListRefsByFile(appFile.Id).Where(r => r.ToName == "mul").ToList();
        edges.Count.ShouldBe(2);
        edges.ShouldAllBe(r => r.FromSymbolId == jsMul.Id);
        edges.ShouldNotContain(r => r.FromSymbolId == rustMul.Id);
    }

    [Fact]
    public void Index_RustCallNames_EdgeWithinTheRustPoolAndNeverAcrossTheWall()
    {
        var workspace = NewPolyglotWorkspace();
        WriteFile(workspace, "repo/src/defs.rs", "pub fn widget() -> u32 {\n    1\n}\n");
        WriteFile(workspace, "repo/src/calls.rs", "pub fn boot() -> u32 {\n    widget();\n    mul(1);\n    0\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var defsFile = store.ListFiles(repoId).Single(f => f.Path == "src/defs.rs");
        var callsFile = store.ListFiles(repoId).Single(f => f.Path == "src/calls.rs");
        var utilFile = store.ListFiles(repoId).Single(f => f.Path == "src/util.js");
        var appFile = store.ListFiles(repoId).Single(f => f.Path == "src/app.js");

        var widget = store.ListSymbolsByFile(defsFile.Id).ShouldHaveSingleItem();
        var jsMul = store.ListSymbolsByFile(utilFile.Id).ShouldHaveSingleItem();
        jsMul.Name.ShouldBe("mul");

        // the mirror of the js side: rust 'widget' finds its rust row, rust 'mul' must not borrow
        // the javascript one — the row stays unmatched rather than becoming a cross-language edge
        var refs = store.ListRefsByFile(callsFile.Id);
        refs.Single(r => r.ToName == "widget").FromSymbolId.ShouldBe(widget.Id);
        refs.Single(r => r.ToName == "mul").FromSymbolId.ShouldBe(0);

        store.ListRefsByFile(appFile.Id).ShouldContain(r => r.ToName == "mul" && r.FromSymbolId == jsMul.Id);
    }

    [Fact]
    public void Index_JavascriptCallIntoTypeScript_EdgeStillSpansTheSharedPool()
    {
        var workspace = NewPolyglotWorkspace();
        WriteFile(workspace, "repo/src/bridge.js", "import { find } from './store';\n\nexport function go() {\n  find(1);\n}\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var bridgeFile = store.ListFiles(repoId).Single(f => f.Path == "src/bridge.js");
        var tsFile = store.ListFiles(repoId).Single(f => f.Path == "src/store.ts");
        var tsFind = store.ListSymbolsByFile(tsFile.Id).Single(s => s.Name == "find");
        tsFind.ContainerName.ShouldBe("Store");

        // javascript, typescript and tsx are one pool: splitting them would silently delete every
        // cross-file edge in a typescript project, and the row would just lose its FromSymbolId
        var refs = store.ListRefsByFile(bridgeFile.Id);
        refs.Count.ShouldBe(2);
        refs.ShouldAllBe(r => r.ToName == "find" && r.FromSymbolId == tsFind.Id);
    }

    [Fact]
    public void Index_TypeScriptTsxRefs_ResolveInsideTheSharedJsPool()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var badgeFile = store.ListFiles(repoId).Single(f => f.Path == "src/Badge.tsx");
        var utilFile = store.ListFiles(repoId).Single(f => f.Path == "src/util.js");
        badgeFile.Language.ShouldBe("tsx");

        var jsMul = store.ListSymbolsByFile(utilFile.Id).ShouldHaveSingleItem();
        var refs = store.ListRefsByFile(badgeFile.Id).Where(r => r.ToName == "mul").ToList();

        // the third member of the family has reference rules too and lives in the same pool, so a
        // .tsx file reaching a .js symbol produces the edge it always produced for .js and .ts
        refs.Count.ShouldBe(2);
        refs.ShouldAllBe(r => r.FromSymbolId == jsMul.Id);
    }

    [Fact]
    public void Index_RustQualifiedGlob_BindsTheModuleRowTheBareGlobAlreadyBound()
    {
        var workspace = NewPolyglotWorkspace();
        WriteFile(workspace, "repo/src/things.rs", "pub mod things {\n    pub fn leaf() {\n    }\n}\n");
        WriteFile(workspace, "repo/src/bare.rs", "use things::*;\n");
        WriteFile(workspace, "repo/src/deep.rs", "use app::crate_path::things::*;\n");
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var moduleRow = store.ListSymbolsByFile(
            store.ListFiles(repoId).Single(f => f.Path == "src/things.rs").Id).Single(s => s.Name == "things");

        // the depth of a glob path used to decide whether it resolved at all: the bare form bound
        // the module row, the qualified one carried 'app::crate_path::things' as its name and hit
        // nothing — one semantics, one binding name, whatever the depth
        var bare = store.ListRefsByFile(store.ListFiles(repoId).Single(f => f.Path == "src/bare.rs").Id);
        var deep = store.ListRefsByFile(store.ListFiles(repoId).Single(f => f.Path == "src/deep.rs").Id);
        bare.ShouldHaveSingleItem().ToName.ShouldBe("things");
        deep.ShouldHaveSingleItem().ToName.ShouldBe("things");
        bare[0].FromSymbolId.ShouldBe(moduleRow.Id);
        deep[0].FromSymbolId.ShouldBe(moduleRow.Id);
        deep[0].ToSymbolId.ShouldBe("app::crate_path");
        bare[0].ToSymbolId.ShouldBeNull();
    }

    [Fact]
    public void Run_Downgrade_FallbackChunksAndAggregatedWarning()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var jsFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/app.js");
        var text = File.ReadAllText(Path.Combine(workspace, "repo", "src", "app.js"));
        var warnings = new List<string>();

        var result = TreeSitterIndexPhase.Run(
            Path.Combine(workspace, "repo"), repoDoc, store, [jsFile],
            new Dictionary<uint, string> { [jsFile.Id] = text },
            SqueezrConfig.CreateDefault().Chunking, noSemantic: false,
            new ThrowingProvider(), warnings.Add);

        result.FailedPaths.ShouldBe(["src/app.js"]);
        warnings.ShouldHaveSingleItem().ShouldContain("tree-sitter failed for 1 file(s), fell back to sliding-window chunks");

        store.ListSymbolsByFile(jsFile.Id).ShouldBeEmpty();

        store.DeleteChunksByFile(jsFile.Id);
        var fallback = FallbackChunker.Chunk(text, SqueezrConfig.CreateDefault().Chunking);
        store.InsertChunks(fallback.Select(c => new ChunkDocument
        {
            FileId = jsFile.Id,
            RepoId = repoDoc.Id,
            StartLine = c.Span.StartLine,
            EndLine = c.Span.EndLine,
            Text = c.Text,
            TokenCount = c.TokenCount,
            ChunkHash = c.ChunkHash,
        }).ToList());
        var chunks = store.ListChunksByFile(jsFile.Id);
        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.SymbolId == null);
    }

    [Fact]
    public void Index_RustFile_PersistsSymbolsAndPerSymbolChunksThroughTreeSitter()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var rustFile = store.ListFiles(repoId).Single(f => f.Path == "src/lib.rs");

        rustFile.Language.ShouldBe("rust");
        rustFile.Kind.ShouldBe("code");

        // WorkspaceIndexer.IsTreeSitterLanguage is what routes .rs here: drop "rust" and the file
        // falls to the sliding-window chunker, leaving zero symbol rows and SymbolId-less chunks
        var symbols = store.ListSymbolsByFile(rustFile.Id);
        symbols.Count.ShouldBe(3);
        symbols.ShouldContain(s => s.Name == "Registry" && s.Kind == "class" && s.ContainerName == null);
        symbols.ShouldContain(s => s.Name == "Registry" && s.Kind == "other" && s.ContainerName == null);
        symbols.ShouldContain(s => s.Name == "lookup" && s.Kind == "method" && s.ContainerName == "Registry");

        var chunks = store.ListChunksByFile(rustFile.Id);
        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.SymbolId != null);
        var lookup = symbols.Single(s => s.Name == "lookup");
        chunks.Single(c => c.StartLine == 8).SymbolId.ShouldBe(lookup.Id);
        chunks.Single(c => c.StartLine == 8).Text.ShouldContain("pub fn lookup(&self, key: &str)");
    }

    [Fact]
    public void Run_RustFileInStore_RebuildsRefRowsAsTreeSitterOwned()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var rustFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/lib.rs");
        var jsFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/app.js");
        store.InsertRefs([new RefDocument
        {
            FromSymbolId = 0,
            ToName = "stale_before_phase",
            Kind = "call",
            FileId = rustFile.Id,
            Line = 3,
        }]);
        var jsText = File.ReadAllText(Path.Combine(workspace, "repo", "src", "app.js"));
        var warnings = new List<string>();

        // IsTreeSitter is the predicate that puts a file's ref rows in this phase's ownership set;
        // drop "rust" from it and the stale row above survives the pass
        TreeSitterIndexPhase.Run(
            Path.Combine(workspace, "repo"), repoDoc, store, [jsFile],
            new Dictionary<uint, string> { [jsFile.Id] = jsText },
            SqueezrConfig.CreateDefault().Chunking, noSemantic: true,
            new TreeSitterProvider(SqueezrConfig.CreateDefault()), warnings.Add);

        store.ListRefsByFile(rustFile.Id).ShouldNotContain(r => r.ToName == "stale_before_phase");
    }

    [Fact]
    public void RefLookup_CrossFileImportAndCallRefs_Reachable()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        var result = new RefLookup(workspace).Find("mul", null);

        var group = result.Groups.ShouldHaveSingleItem();
        group.Name.ShouldBe("mul");
        var definition = group.Definitions.ShouldHaveSingleItem();
        definition.Kind.ShouldBe("function");
        definition.File.ShouldBe("src/util.js");
        group.Refs.ShouldContain(r =>
            r.File == "src/app.js" && r.Line == 1 && r.Kind == "import");
        group.Refs.ShouldContain(r =>
            r.File == "src/app.js" && r.Line == 9 && r.Kind == "call" && r.EnclosingSymbol == "Calc.double");
    }

    [Fact]
    public void Index_IncrementalEditOfCallee_KeepsIncomingRefsFromUntouchedFile()
    {
        var workspace = NewPolyglotWorkspace();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        WriteFile(workspace, "repo/src/util.js", "export function mul(a, b) {\n  return a * b * 2;\n}\n");
        indexer.Index(workspace);

        var result = new RefLookup(workspace).Find("mul", null);
        var group = result.Groups.ShouldHaveSingleItem();
        group.Definitions.ShouldHaveSingleItem().File.ShouldBe("src/util.js");
        group.Refs.ShouldContain(r => r.File == "src/app.js" && r.Line == 1 && r.Kind == "import");
        group.Refs.ShouldContain(r => r.File == "src/app.js" && r.Line == 9 && r.Kind == "call");
    }

    [Fact]
    public void IndexFile_EditOfCallee_KeepsIncomingRefsFromUntouchedFile()
    {
        var workspace = NewPolyglotWorkspace();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        WriteFile(workspace, "repo/src/util.js", "export function mul(a, b) {\n  return a * b * 2;\n}\n");
        indexer.IndexFile(workspace, "demo", "src/util.js");

        var result = new RefLookup(workspace).Find("mul", null);
        var group = result.Groups.ShouldHaveSingleItem();
        group.Refs.ShouldContain(r => r.File == "src/app.js" && r.Kind == "import");
        group.Refs.ShouldContain(r => r.File == "src/app.js" && r.Kind == "call");
    }

    [Fact]
    public void Index_NoSemantic_JsGetsFallbackChunksWithoutSymbols()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace, noSemantic: true);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var jsFile = store.ListFiles(repoId).Single(f => f.Path == "src/app.js");
        var csFile = store.ListFiles(repoId).Single(f => f.Path == "src/a.cs");

        store.ListSymbolsByFile(jsFile.Id).ShouldBeEmpty();
        var jsChunks = store.ListChunksByFile(jsFile.Id);
        jsChunks.ShouldNotBeEmpty();
        jsChunks.ShouldAllBe(c => c.SymbolId == null);
        store.ListSymbolsByFile(csFile.Id).ShouldNotBeEmpty();
    }

    [Fact]
    public void Index_NodeModuleVariants_SymbolsResolveAndCrossFileImportIsReachable()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoId = store.FindRepo("demo")!.Id;
            var mjsFile = store.ListFiles(repoId).Single(f => f.Path == "tools/a.mjs");
            var cjsFile = store.ListFiles(repoId).Single(f => f.Path == "tools/b.cjs");
            var mainFile = store.ListFiles(repoId).Single(f => f.Path == "tools/main.mjs");

            mjsFile.Language.ShouldBe("javascript");
            cjsFile.Language.ShouldBe("javascript");
            mainFile.Language.ShouldBe("javascript");

            store.ListSymbolsByFile(mjsFile.Id).ShouldContain(s => s.Name == "build" && s.Kind == "function");
            store.ListSymbolsByFile(cjsFile.Id).ShouldContain(s => s.Name == "run" && s.Kind == "function");
            store.ListSymbolsByFile(mainFile.Id).ShouldContain(s => s.Name == "serve" && s.Kind == "function");
        }

        var build = new RefLookup(workspace).Find("build", null);
        var buildGroup = build.Groups.ShouldHaveSingleItem();
        buildGroup.Definitions.ShouldHaveSingleItem().File.ShouldBe("tools/a.mjs");
        buildGroup.Refs.ShouldContain(r => r.File == "tools/main.mjs" && r.Kind == "import");

        var run = new RefLookup(workspace).Find("run", null);
        run.Groups.ShouldHaveSingleItem().Definitions.ShouldHaveSingleItem().File.ShouldBe("tools/b.cjs");
    }

    private static string NewPolyglotWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/src/a.cs", "class Calc {\n    int Add(int a, int b) {\n        return a + b;\n    }\n}\n");
        WriteFile(workspace, "repo/src/app.js", """
            import { mul } from './util';

            export function add(a, b) {
              return a + b;
            }

            export class Calc {
              double(n) {
                return mul(2, n);
              }
            }
            """);
        WriteFile(workspace, "repo/src/util.js", "export function mul(a, b) {\n  return a * b;\n}\n");
        WriteFile(workspace, "repo/src/store.ts", """
            interface User {
              id: number;
            }

            export class Store {
              find(id: number): User | undefined {
                return undefined;
              }
            }
            """);
        WriteFile(workspace, "repo/src/Badge.tsx", """
            import { mul } from './util';

            export function Badge({ count }: { count: number }) {
              return <div className="badge">{mul(count, 2)}</div>;
            }
            """);
        WriteFile(workspace, "repo/src/lib.rs", """
            use std::collections::HashMap;

            pub struct Registry {
                entries: HashMap<String, String>,
            }

            impl Registry {
                pub fn lookup(&self, key: &str) -> Option<&String> {
                    self.entries.get(key)
                }
            }
            """);
        WriteFile(workspace, "repo/docs/notes.md", "alpha\nbeta\ngamma\n");
        WriteFile(workspace, "repo/tools/a.mjs", "export async function build() {\n  return 'artifact';\n}\n");
        WriteFile(workspace, "repo/tools/b.cjs", "module.exports = function run() {\n  return 'ready';\n};\n");
        WriteFile(workspace, "repo/tools/main.mjs", """
            import { build } from './a.mjs';

            export function serve() {
              return build();
            }
            """);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class ThrowingProvider : IParsingProviderWithWarnings
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public bool CanHandle(string path, string? languageHint) => true;

        public ParseResult Parse(FileRef file, ReadOnlySpan<byte> content)
        {
            _warnings.Add($"parse failed for '{file.Path}': grammar exploded");
            return new ParseResult("javascript", [], [], []);
        }
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
