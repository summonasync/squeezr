using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpSymbolMembersTests
{
    private const int MembersFitBudget = 120;
    private const int MethodCount = 12;

    [Fact]
    public async Task SymbolLookup_OversizedType_CarriesMembersInsteadOfDeadEnd()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Huge", ["maxTokens"] = MembersFitBudget },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        var members = hit.GetProperty("members").EnumerateArray().ToList();
        members.Count.ShouldBe(MethodCount);
        members[0].GetProperty("name").GetString().ShouldBe("Method01");
        members[0].GetProperty("kind").GetString().ShouldBe("method");
        members[0].GetProperty("line").GetInt32().ShouldBe(5);

        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)
            .ShouldContain("read a member with expand_span or raise maxTokens");
    }

    [Fact]
    public async Task SymbolLookup_BodyFits_LeavesMembersOut()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Huge", ["maxTokens"] = 600 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("body").GetString()!.ShouldContain($"return a + {MethodCount};");
        hit.TryGetProperty("members", out _).ShouldBeFalse();
        hit.TryGetProperty("membersOmitted", out _).ShouldBeFalse();
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Huge.cs", HugeClass());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string HugeClass()
    {
        var text = new StringBuilder("namespace Demo;\n\npublic class Huge\n{\n");
        foreach (var index in Enumerable.Range(1, MethodCount))
        {
            text.Append($"    public int Method{index:00}(int a)\n");
            text.Append("    {\n");
            text.Append($"        return a + {index};\n");
            text.Append("    }\n\n");
        }

        return text.Append("}\n").ToString();
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
