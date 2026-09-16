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
            allChunks.ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == "onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5");
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
    public void RefLookup_CrossFileImportAndCallRefs_Reachable()
    {
        var workspace = NewPolyglotWorkspace();
        new WorkspaceIndexer().Index(workspace);

        var result = new RefLookup(workspace).Find("mul", null);

        var group = result.Groups.ShouldHaveSingleItem();
        group.Name.ShouldBe("mul");
        group.Kind.ShouldBe("function");
        group.File.ShouldBe("src/util.js");
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
        group.File.ShouldBe("src/util.js");
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

    private static string NewPolyglotWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
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
        WriteFile(workspace, "repo/docs/notes.md", "alpha\nbeta\ngamma\n");
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

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
