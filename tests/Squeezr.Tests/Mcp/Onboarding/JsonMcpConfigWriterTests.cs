using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using System.Text.Json.Nodes;

namespace Squeezr.Tests.Mcp.Onboarding;

public class JsonMcpConfigWriterTests
{
    private const string CommandPath = "/opt/squeezr/bin/squeezr";
    private const string WorkspaceRoot = "/work/demo";
    private const string ServerName = "squeezr";

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Mcp", "Onboarding", "Fixtures", "json");

    [Fact]
    public async Task Install_MissingFile_CreatesConfigWithSqueezr()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "nested", "mcp.json");

        await InstallAsync(path, McpClientId.Claude);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("new-file.expected.json"));
    }

    [Fact]
    public async Task Install_EmptyFile_CreatesConfigWithSqueezr()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "mcp.json");
        await File.WriteAllTextAsync(path, string.Empty);

        await InstallAsync(path, McpClientId.Claude);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("new-file.expected.json"));
    }

    [Fact]
    public async Task Install_ClaudeUserFile_MergesOnlyMcpServers()
    {
        var path = CopyToTemp("claude-user.input.json");

        await InstallAsync(path, McpClientId.Claude);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("claude-user.expected.json"));
    }

    [Fact]
    public async Task Install_ExistingServers_PreservesOtherServers()
    {
        var path = CopyToTemp("existing-servers.input.json");

        await InstallAsync(path, McpClientId.Claude);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("existing-servers.expected.json"));
    }

    [Fact]
    public async Task Install_ExistingSqueezrEntry_ReplacesInPlace()
    {
        var path = CopyToTemp("replace-squeezr.input.json");
        var original = File.ReadAllText(path);

        await InstallAsync(path, McpClientId.Claude);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("replace-squeezr.expected.json"));
        Normalize(File.ReadAllText(path)).ShouldNotBe(Normalize(original));
    }

    [Fact]
    public async Task Install_QwenSettings_PreservesOtherSections()
    {
        var path = CopyToTemp("qwen-settings.input.json");

        await InstallAsync(path, McpClientId.Qwen);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("qwen-settings.expected.json"));
    }

    [Fact]
    public async Task Install_Twice_ProducesIdenticalFile()
    {
        var path = CopyToTemp("existing-servers.input.json");
        var entry = BuildEntry(McpClientId.Claude);

        await JsonMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
        var first = File.ReadAllText(path);
        await JsonMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
        var second = File.ReadAllText(path);

        second.ShouldBe(first);
        Normalize(second).ShouldBe(Expected("existing-servers.expected.json"));
    }

    [Fact]
    public async Task Remove_MixedServers_KeepsOtherServers()
    {
        var path = CopyToTemp("remove-mixed.input.json");

        await JsonMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("remove-mixed.expected.json"));
    }

    [Fact]
    public async Task Remove_LastServerEntry_LeavesEmptyMcpServers()
    {
        var path = CopyToTemp("remove-last.input.json");

        await JsonMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("remove-last.expected.json"));
    }

    [Fact]
    public async Task Remove_AbsentEntry_LeavesFileUntouched()
    {
        var path = CopyToTemp("existing-servers.input.json");
        var original = File.ReadAllText(path);

        await JsonMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        File.ReadAllText(path).ShouldBe(original);
    }

    [Fact]
    public async Task Remove_MissingFile_DoesNotCreateFile()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "mcp.json");

        await JsonMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_OverExistingFile_CreatesBackupWithOriginalContent()
    {
        var path = CopyToTemp("existing-servers.input.json");
        var original = File.ReadAllText(path);

        await InstallAsync(path, McpClientId.Claude);

        File.Exists(path + ".bak").ShouldBeTrue();
        File.ReadAllText(path + ".bak").ShouldBe(original);
    }

    [Fact]
    public async Task Install_NonObjectRoot_Throws()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "mcp.json");
        await File.WriteAllTextAsync(path, "[]");

        await Should.ThrowAsync<InvalidDataException>(
            () => InstallAsync(path, McpClientId.Claude));
    }

    [Fact]
    public async Task Install_NonObjectMcpServers_Throws()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "mcp.json");
        await File.WriteAllTextAsync(path, """{"mcpServers": []}""");

        await Should.ThrowAsync<InvalidDataException>(
            () => InstallAsync(path, McpClientId.Claude));
    }

    [Fact]
    public async Task Install_DuplicateKeys_ThrowsWithConfigPath()
    {
        var path = CopyToTemp("duplicate-keys.input.json");
        var original = File.ReadAllText(path);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => InstallAsync(path, McpClientId.Claude));

        exception.Message.ShouldContain(path);
        File.ReadAllText(path).ShouldBe(original);
    }

    private static async Task InstallAsync(string path, McpClientId clientId)
    {
        var entry = BuildEntry(clientId);
        await JsonMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
    }

    private static JsonObject BuildEntry(McpClientId clientId) =>
        ServerEntryBuilder.Build(McpClientRegistry.Get(clientId), WorkspaceRoot, CommandPath, watch: true);

    private static string CopyToTemp(string fixtureName)
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "config.json");
        File.Copy(Path.Combine(FixturesDir, fixtureName), path);
        return path;
    }

    private static string Expected(string fixtureName) =>
        Normalize(File.ReadAllText(Path.Combine(FixturesDir, fixtureName)));

    private static string Normalize(string content) => content.TrimEnd('\r', '\n');
}
