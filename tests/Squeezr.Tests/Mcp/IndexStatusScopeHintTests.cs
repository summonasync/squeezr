using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

[Collection("ProcessGlobals")]
public sealed class IndexStatusScopeHintTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string WorkspaceName = "hint-ws";
    private const string HintMarker = "other live workspaces";

    private readonly string? _previousHome;
    private readonly string _home;

    public IndexStatusScopeHintTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task IndexStatus_OneRepoWithALiveSibling_CarriesTheAddRemedyAndTheServeAlternative()
    {
        var workspace = NewWorkspace(repoCount: 1);
        var sibling = AddRegistryRow("hint-neighbor");

        var warnings = await WarningsOf(workspace);

        var hint = HintLines(warnings).ShouldHaveSingleItem();
        hint.ShouldContain($"squeezr add '{sibling.RootPath}' --name hint-neighbor");
        hint.ShouldContain($"cd '{sibling.RootPath}' && squeezr serve");
        hint.ShouldContain("squeezr add <path> --name <repo>");
        hint.ShouldNotContain("squeezr init");
    }

    [Fact]
    public async Task IndexStatus_WorkspaceWithTwoRepos_NoScopeHint()
    {
        var workspace = NewWorkspace(repoCount: 2);
        AddRegistryRow("hint-neighbor");

        var warnings = await WarningsOf(workspace);

        HintLines(warnings).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexStatus_SingleWorkspaceRegistry_NoScopeHint()
    {
        var workspace = NewWorkspace(repoCount: 1);

        var warnings = await WarningsOf(workspace);

        HintLines(warnings).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexStatus_AllSiblingsStale_NoScopeHint()
    {
        var workspace = NewWorkspace(repoCount: 1);
        AddRegistryRow("hint-gone-a", live: false);
        AddRegistryRow("hint-gone-b", live: false);

        var warnings = await WarningsOf(workspace);

        HintLines(warnings).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexStatus_FiveLiveSiblings_OneHintLineNamesAtMostThree()
    {
        var workspace = NewWorkspace(repoCount: 1);
        AddRegistryRow("hint-sib-alpha");
        AddRegistryRow("hint-sib-bravo");
        AddRegistryRow("hint-sib-charlie");
        AddRegistryRow("hint-sib-delta");
        AddRegistryRow("hint-sib-echo");

        var warnings = await WarningsOf(workspace);

        var hint = HintLines(warnings).ShouldHaveSingleItem();
        hint.ShouldContain("hint-sib-alpha, hint-sib-bravo, hint-sib-charlie");
        hint.ShouldNotContain("hint-sib-delta");
        hint.ShouldNotContain("hint-sib-echo");
    }

    private static List<string> HintLines(IReadOnlyList<string> warnings) =>
        [.. warnings.Where(warning => warning.Contains(HintMarker, StringComparison.Ordinal))];

    private async Task<List<string>> WarningsOf(string workspace)
    {
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return [.. JsonDocument.Parse(json).RootElement.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetString()!)];
    }

    private static string NewWorkspace(int repoCount)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(WorkspaceName);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        for (var index = 0; index < repoCount; index++)
        {
            var repoDir = $"repo-{index}";
            Directory.CreateDirectory(Path.Combine(workspace, repoDir));
            File.WriteAllText(Path.Combine(workspace, repoDir, "A.cs"), $"class HintA{index} {{ }}");
            manager.AddRepo(repoDir, $"hint-repo-{index}");
        }

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static WorkspaceRegistryEntry AddRegistryRow(string name, bool live = true)
    {
        var root = WorkspaceCanonicalPath.Canonicalize(TestDirs.NewTempDir());
        if (!live)
        {
            Directory.Delete(root);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new WorkspaceRegistryEntry(name, root, WorkspaceStoreKeys.Create(name, root), now, now);
        WorkspaceRegistry.Add(entry);
        return entry;
    }
}
