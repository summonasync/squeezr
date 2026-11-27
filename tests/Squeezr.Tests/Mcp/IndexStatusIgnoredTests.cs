using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Mcp;

public class IndexStatusIgnoredTests
{
    [Fact]
    public async Task IndexStatus_WithGitignore_ShowsIgnoredBlockAndWarning()
    {
        var workspace = NewWorkspaceWithGitignore();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var repo = envelope.GetProperty("data").GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();

        repo.TryGetProperty("ignored", out var ignored).ShouldBeTrue();
        ignored.GetProperty("files").GetInt32().ShouldBe(2);
        var bySource = ignored.GetProperty("bySource");
        bySource.TryGetProperty("gitignore", out var gitCount).ShouldBeTrue();
        gitCount.GetInt32().ShouldBe(2);
        bySource.EnumerateObject().Sum(p => p.Value.GetInt32()).ShouldBe(2);

        var warnings = envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).ToList();
        warnings.ShouldContain(w => w.Contains("last index pass excluded") && w.Contains(".gitignore"));
    }

    [Fact]
    public async Task IndexStatus_WithRespectGitignoreFalse_NoGitignoreWarning()
    {
        var workspace = NewWorkspaceWithGitignore(respectGitignore: false);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var warnings = envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).ToList();
        warnings.ShouldNotContain(w => w.Contains("via .gitignore"));
    }

    [Fact]
    public async Task IndexStatus_NoIgnoredFiles_OmitsIgnoredBlock()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var repo = envelope.GetProperty("data").GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.TryGetProperty("ignored", out _).ShouldBeFalse();
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewWorkspaceWithGitignore(bool respectGitignore = true)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);

        var config = SemanticConfigFile.Load(workspace);
        SemanticConfigFile.Save(workspace, config with
        {
            Indexing = config.Indexing with { RespectGitignore = respectGitignore }
        });

        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        WriteFile(workspace, "repo/web/out/bundle.js", "var x = 1;");
        WriteFile(workspace, "repo/web/out/vendor.js", "var y = 2;");
        File.WriteAllText(Path.Combine(workspace, "repo", ".gitignore"), "web/out/\n");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
