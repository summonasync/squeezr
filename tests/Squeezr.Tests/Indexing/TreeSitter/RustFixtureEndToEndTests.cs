using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Store-read rows come back unordered (T2 obstacle), so every assertion here is set-based or ordered
// explicitly; the per-file counts below were measured by indexing the fixture, not hand-computed.
public class RustFixtureEndToEndTests
{
    private const string JsTwinSource = """
        import { mul } from './registry';

        export class Ledger {
          constructor(entries) {
            this.entries = entries;
          }

          post(entry) {
            return mul(entry, 2);
          }

          close() {
            return this.entries.length;
          }
        }

        export function makeLedger(entries) {
          return new Ledger(entries);
        }

        export const idle = () => 0;
        """;

    [Fact]
    public void Index_RustCrate_PersistsEveryFileAsRustCodeWithPerSymbolChunks()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        files.Keys.ShouldContain("src/registry.rs");
        files["Cargo.toml"].Kind.ShouldBe("docs");
        new[] { "src/lib.rs", "src/registry.rs", "src/engine.rs", "src/prelude.rs", "src/prelude/model.rs" }
            .ShouldAllBe(path => files[path].Language == "rust" && files[path].Kind == "code");

        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in files.Keys.Where(p => p.EndsWith(".rs", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileSymbols = store.ListSymbolsByFile(files[path].Id);
            var fileChunks = store.ListChunksByFile(files[path].Id);
            symbols[path] = fileSymbols.Count;

            // granularity claim: one chunk per symbol, every chunk owned by a symbol, nothing orphaned
            fileChunks.Count.ShouldBe(fileSymbols.Count);
            fileChunks.ShouldAllBe(c => c.SymbolId != null);

            // embed side, measured per rust file: the embedding phase is language-blind, so a rust
            // chunk must leave the run as embedded rather than pending — without this the claim that
            // rust chunks carry embeddings rests on the read-side mapping alone
            fileChunks.ShouldAllBe(c => c.VectorStatus == "ok" && c.ModelKey != null);

            foreach (var symbol in fileSymbols)
            {
                fileChunks.ShouldContain(c => c.StartLine == symbol.StartLine && c.EndLine == symbol.EndLine);
            }
        }

        symbols.ShouldBe(new Dictionary<string, int>
        {
            ["src/engine.rs"] = 8,
            ["src/lib.rs"] = 6,
            ["src/prelude.rs"] = 5,
            ["src/prelude/model.rs"] = 8,
            ["src/registry.rs"] = 18,
        });
    }

    [Fact]
    public void SymbolLookup_KnownRustFunction_AnswersWithSignatureSpanAndContainer()
    {
        var workspace = NewIndexedFixture();

        var result = new SymbolLookup(workspace).Find("run", null, null, 400, body: false);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.Repo.ShouldBe("demo");
        hit.File.ShouldBe("src/engine.rs");
        hit.Name.ShouldBe("run");
        hit.Kind.ShouldBe("function");
        hit.ContainerName.ShouldBe("pipeline.stage");
        hit.Signature.ShouldBe("pub fn run(registry: &crate::registry::Registry) -> String");
        hit.StartLine.ShouldBe(3);
        hit.EndLine.ShouldBe(5);
    }

    [Fact]
    public void FindReferences_RustTraitMethod_ListsCallersAcrossFilesWithEnclosing()
    {
        var workspace = NewIndexedFixture();

        var result = new RefLookup(workspace).Find("render", null);

        // two same-name declarations (trait method and its impl) share one call list — the fan-out
        // shape find_references already ships for C# and js, so rust has to land in it, not beside it
        var group = result.Groups.ShouldHaveSingleItem();
        group.Definitions.Select(d => $"{d.File}:{d.StartLine}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["src/registry.rs:13", "src/registry.rs:29"]);
        group.Refs.Select(r => $"{r.File}:{r.Line}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["src/engine.rs:4", "src/lib.rs:18"]);
        group.Refs.ShouldAllBe(r => r.Kind == "call");
        group.Refs.Single(r => r.File == "src/engine.rs").EnclosingSymbol.ShouldBe("stage.run");
    }

    [Fact]
    public void CodeSearch_ExactArm_FindsRustChunkByIdentifier()
    {
        var workspace = NewIndexedFixture();

        var response = new SearchEngine(workspace).Search(
            new SearchQuery("with_capacity", false, null, null, null, null, null, null));

        // the exact arm reaches a rust chunk the same way it reaches a js one: file, line, snippet
        response.Matches.ShouldContain(m => m.Path == "src/registry.rs");
        var hit = response.Matches.Single(m => m.Path == "src/registry.rs");
        hit.Repo.ShouldBe("demo");
        hit.Line.ShouldBe(19);
        hit.Snippet.ShouldContain("with_capacity");
    }

    [Fact]
    public async Task CodeSearch_SemanticArm_FindsRustChunkWithoutATermHit()
    {
        var workspace = NewIndexedFixture(out var store);
        var repoId = store.FindRepo("demo")!.Id;
        var registry = store.ListFiles(repoId).Single(f => f.Path == "src/registry.rs");
        var chunk = store.ListChunksByFile(registry.Id).Single(c => c.StartLine == 28);
        store.Dispose();

        var response = await new SearchEngine(workspace).SearchHybridAsync(new SearchQuery("unmatched paraphrase zzz", false, null, null, null, null, null, null), TestVectorPhase.Arm(workspace, new FakeEmbeddingProvider(), new OneHitVectorStore(repoId, chunk)), CancellationToken.None);

        // the rust chunks carry embeddings like every other code file, so the semantic arm answers
        // for rust on a query no term match could reach
        var hit = response.Matches.Single(m => m.Path == "src/registry.rs");
        hit.Source.ShouldBe("semantic");
        hit.Snippet.ShouldContain("impl Summary for Registry");
    }

    [Fact]
    public void Index_QualifiedGenericImplTarget_BindsTheBareStructRow()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var registry = store.ListFiles(repoId).Single(f => f.Path == "src/registry.rs");
        var engine = store.ListFiles(repoId).Single(f => f.Path == "src/engine.rs");
        var registryRows = store.ListSymbolsByFile(registry.Id);
        var structRow = store.ListSymbolsByFile(engine.Id).Single(s => s.Name == "Holder" && s.Kind == "class");
        var registryTargets = store.ListRefsByFile(registry.Id)
            .Where(r => r.Line == 54 && r.ToName == "Registry" && r.Kind == "reference")
            .Select(r => r.FromSymbolId)
            .ToList();

        // review-111-04 F1: 'impl<T> app::Holder<T>' is a use of Holder, not of the string
        // 'app::Holder' — the bare name is what reaches the class row across files, while the impl's
        // own symbol row keeps the qualifier and the arity the member lookup matches on
        store.ListRefsByFile(registry.Id).ShouldContain(r =>
            r.Line == 48 && r.ToName == "Holder" && r.FromSymbolId == structRow.Id);
        registryRows.ShouldNotContain(s => s.Name == "Holder");
        registryRows.Single(s => s.Kind == "other" && s.Signature == "impl<T> app::Holder<T>")
            .Name.ShouldBe("app::Holder<T>");

        // 'impl<T> serde::Serialize<T> for Registry': trait keeps its bare name and the module path,
        // self type reaches both same-name rows of the type (class and impl) as every rust name does
        var registryStruct = registryRows.Single(s => s.Name == "Registry" && s.Kind == "class");
        registryTargets.ShouldContain(registryStruct.Id);
        registryTargets.Count.ShouldBe(2);
        store.ListRefsByFile(registry.Id).Single(r => r.Line == 54 && r.Kind == "implement")
            .ToSymbolId.ShouldBe("serde");
    }

    [Fact]
    public void Index_QualifiedAndBareGlobs_BindTheSameModuleRow()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var registry = store.ListFiles(repoId).Single(f => f.Path == "src/registry.rs");
        var moduleRow = store.ListSymbolsByFile(registry.Id).Single(s => s.Name == "things");

        // review-111-04 F2: path depth lives in the module column only, so 'use things::*' and
        // 'use crate::registry::things::*' bind the same row instead of resolving at one depth only
        var globs = store.ListRefsByFile(registry.Id)
            .Where(r => r.Line == 5 || r.Line == 6)
            .OrderBy(r => r.Line)
            .ToList();
        globs.Select(r => r.ToName).ShouldBe(["things", "things"]);
        globs.ShouldAllBe(r => r.FromSymbolId == moduleRow.Id);
        globs[0].ToSymbolId.ShouldBe("crate::registry");
        globs[1].ToSymbolId.ShouldBeNull();
    }

    [Fact]
    public void Index_DeclaredShapes_TypeWithInherentImpl_KeepsOneRowPerShape()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var registry = store.ListFiles(repoId).Single(f => f.Path == "src/registry.rs");

        // deliberate, not a duplicate to clean up: rust splits a type across the struct item and the
        // impl blocks the way C# splits it across partial classes
        var registryRows = store.ListSymbolsByFile(registry.Id)
            .Where(s => s.Name == "Registry")
            .Select(s => s.Kind)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        registryRows.ShouldBe(["class", "other"]);
    }

    [Fact]
    public void FindReferences_RustMacro_DefinitionAnswersForInvocationSites()
    {
        var workspace = NewIndexedFixture();

        var result = new RefLookup(workspace).Find("note", null);

        // the macro is defined in lib.rs and shouted from registry.rs: an invocation is a call site
        // of the definition row, which is what makes find_references useful on macro_rules at all
        var group = result.Groups.ShouldHaveSingleItem();
        group.Definitions.ShouldHaveSingleItem().File.ShouldBe("src/lib.rs");
        group.Definitions.ShouldHaveSingleItem().Kind.ShouldBe("other");
        group.Refs.ShouldContain(r => r.File == "src/registry.rs" && r.Line == 24 && r.Kind == "call");
    }

    [Fact]
    public void FindReferences_CommonRustName_FansOutToEverySameNameDefinition()
    {
        var workspace = NewIndexedFixture();

        var result = new RefLookup(workspace).Find("take", null);

        // three types in three files each own a 'take'; the lookup answers with all of them rather
        // than pretending one is 'the' definition, and unresolved std rows stay unresolved
        var definitions = result.Groups.SelectMany(g => g.Definitions).ToList();
        definitions.Select(d => $"{d.File}:{d.StartLine}").OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["src/engine.rs:15", "src/prelude/model.rs:10", "src/registry.rs:49"]);
    }

    [Fact]
    public void Index_UnresolvedForeignNames_StayAsRowsAndAreNeverDropped()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var registry = files["src/registry.rs"];

        // a rust crate references std more than it references itself, so 'every ref resolves' is a
        // false expectation here; the rows must exist with FromSymbolId 0 and the crate's own edges
        // must resolve next to them in the same set. Denominator is the crate's rust files only —
        // the js twin in the same repo is the parity test's business, not this count
        var rustRefs = files
            .Where(entry => entry.Key.EndsWith(".rs", StringComparison.Ordinal))
            .SelectMany(entry => store.ListRefsByFile(entry.Value.Id))
            .ToList();
        rustRefs.Count.ShouldBe(53);
        rustRefs.ShouldContain(r => r.ToName == "with_capacity" && r.FromSymbolId == 0);
        rustRefs.ShouldContain(r => r.ToName == "to_string" && r.FromSymbolId == 0);
        rustRefs.ShouldContain(r =>
            r.FileId == registry.Id && r.ToName == "spare" && r.Kind == "call" && r.FromSymbolId != 0);
    }

    [Fact]
    public void Index_ConstructionExpressions_ProduceNoEdgeInEitherLanguage()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var lib = store.ListFiles(repoId).Single(f => f.Path == "src/lib.rs");

        // lib.rs:15 builds a Registry with a struct expression and the js twin builds one with
        // 'new': neither becomes a reference row, so the two families stay symmetric on this axis.
        // Each side gets its denominator first — an empty row set would satisfy the negative too
        var libRefs = store.ListRefsByFile(lib.Id);
        libRefs.ShouldNotBeEmpty();
        libRefs.ShouldNotContain(r => r.Line == 15);
        var twin = ParseTwin();
        twin.Refs.ShouldNotBeEmpty();
        twin.Refs.ShouldNotContain(r => r.ToName == "Ledger");
        twin.Symbols.Select(s => s.Name).ShouldContain("Ledger");
    }

    [Fact]
    public void Index_RustAndJsTwin_MatchOnPerSymbolChunkGranularity()
    {
        var workspace = NewIndexedFixture();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var rust = files["src/registry.rs"];
        var js = files["web/ledger.js"];
        var rustSymbols = store.ListSymbolsByFile(rust.Id);
        var jsSymbols = store.ListSymbolsByFile(js.Id);

        jsSymbols.ShouldNotBeEmpty();
        foreach (var (fileId, symbols) in new[] { (rust.Id, rustSymbols), (js.Id, jsSymbols) })
        {
            var chunks = store.ListChunksByFile(fileId);
            chunks.Count.ShouldBe(symbols.Count);
            chunks.ShouldAllBe(c => c.SymbolId != null);
            symbols.ShouldAllBe(s => chunks.Any(c => c.StartLine == s.StartLine && c.EndLine == s.EndLine));
        }
    }

    private static ParseResult ParseTwin() =>
        new TreeSitterProvider(Squeezr.Semantic.Configuration.SemanticConfig.Bind(Squeezr.Core.Configuration.SqueezrConfig.CreateDefault()).Core)
            .Parse(new FileRef("demo", "web/ledger.js"), Encoding.UTF8.GetBytes(JsTwinSource));

    private static string NewIndexedFixture() => NewIndexedFixture(out _);

    private static string NewIndexedFixture(out MetaStore store)
    {
        var workspace = TestDirs.NewTempDir();
        var repo = Path.Combine(workspace, "repo");
        Directory.CreateDirectory(repo);
        CopyFixture(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RustMini"), repo);
        WriteFile(workspace, "repo/web/ledger.js", JsTwinSource);

        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(
            workspace, vectorPhase: TestVectorPhase.Runtime(new FakeEmbeddingProvider(), new RecordingVectorStore()));

        store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return workspace;
    }

    private static void CopyFixture(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.Calibrated;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

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

    private sealed class OneHitVectorStore : IVectorStore
    {
        private readonly VectorHit _hit;

        public OneHitVectorStore(uint repoId, ChunkDocument chunk) =>
            _hit = new VectorHit(1, chunk.ChunkHash, chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.9);

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([_hit]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(1L);

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
