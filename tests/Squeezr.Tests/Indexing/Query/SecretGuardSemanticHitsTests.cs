using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class SecretGuardSemanticHitsTests
{
    [Fact]
    public async Task Resolve_ProtectedChunkInStore_IsNotSurfaced()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var chunk = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.EnvPath, SecretGuardFixture.EnvCanary);

        (await Resolve(workspace, chunk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_OrdinaryChunk_StillSurfacesItsText()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var chunk = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.AppPath, SecretGuardFixture.AppCanary);

        var hits = await Resolve(workspace, chunk);

        var hit = hits.ShouldHaveSingleItem();
        hit.Match.Path.ShouldBe(SecretGuardFixture.AppPath);
        hit.Match.Snippet.ShouldContain(SecretGuardFixture.AppCanary);
    }

    [Fact]
    public async Task Resolve_ExtraPatternCoversTheChunkFile_IsNotSurfaced()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var chunk = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.TokensPath, SecretGuardFixture.TokensCanary);
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        (await Resolve(workspace, chunk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_SkipsProtectedAndFillsTheArmFromTheNextHit()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var env = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.EnvPath, SecretGuardFixture.EnvCanary);
        var app = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.AppPath, SecretGuardFixture.AppCanary);

        var hits = await SemanticHitsResolver.Resolve(
            workspace,
            new SearchQuery("zznotaword", false, null, null, null, null, null, null),
            new SecretGuardFixture.FixedEmbeddings(),
            new SecretGuardFixture.ScriptedVectorStore
            {
                Hits =
                [
                    new VectorHit(1, "a", env.FileId, env.RepoId, env.StartLine, env.EndLine, 0.95),
                    new VectorHit(2, "b", app.FileId, app.RepoId, app.StartLine, app.EndLine, 0.9)
                ]
            },
            CancellationToken.None,
            null,
            SecretGuardReader.Load(workspace));

        hits.ShouldHaveSingleItem().Match.Path.ShouldBe(SecretGuardFixture.AppPath);
    }

    private static async Task<List<(string Key, SearchMatch Match)>> Resolve(
        string workspace, (uint FileId, uint RepoId, int StartLine, int EndLine) chunk)
    {
        return await SemanticHitsResolver.Resolve(
            workspace,
            new SearchQuery("zznotaword", false, null, null, null, null, null, null),
            new SecretGuardFixture.FixedEmbeddings(),
            new SecretGuardFixture.ScriptedVectorStore
            {
                Hits = [new VectorHit(1, "h", chunk.FileId, chunk.RepoId, chunk.StartLine, chunk.EndLine, 0.9)]
            },
            CancellationToken.None,
            null,
            SecretGuardReader.Load(workspace));
    }
}
