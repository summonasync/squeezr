using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using System.Text.Json.Nodes;

namespace Squeezr.Tests.Mcp.Onboarding;

public class ServerEntryBuilderTests
{
    private const string CommandPath = "/opt/squeezr/bin/squeezr";

    [Fact]
    public void Build_ClaudeEntry_HasCommandAndWatchArgsWithoutExtras()
    {
        var entry = Build(McpClientId.Claude, watch: true);

        Command(entry).ShouldBe(CommandPath);
        Args(entry).ShouldBe(["serve", "--watch"]);
        entry.ContainsKey("cwd").ShouldBeFalse();
        entry.ContainsKey("versionNegotiation").ShouldBeFalse();
        entry.ContainsKey("startup_timeout_sec").ShouldBeFalse();
    }

    [Fact]
    public void Build_WithoutWatch_ArgsContainServeOnly()
    {
        var entry = Build(McpClientId.Claude, watch: false);

        Args(entry).ShouldBe(["serve"]);
    }

    [Theory]
    [InlineData(McpClientId.Kimi)]
    [InlineData(McpClientId.Qwen)]
    [InlineData(McpClientId.Codex)]
    public void Build_CwdSupportingClients_AbsoluteCurrentDirectory(McpClientId clientId)
    {
        var entry = ServerEntryBuilder.Build(
            McpClientRegistry.Get(clientId), "relative-ws", CommandPath, watch: true);

        entry["cwd"]!.GetValue<string>().ShouldBe(Path.GetFullPath("relative-ws"));
    }

    [Fact]
    public void Build_QwenEntry_HasVersionNegotiationAuto()
    {
        var entry = Build(McpClientId.Qwen, watch: true);

        entry["versionNegotiation"]!.GetValue<string>().ShouldBe("auto");
    }

    [Fact]
    public void Build_CodexEntry_HasStartupTimeout30()
    {
        var entry = Build(McpClientId.Codex, watch: true);

        entry["startup_timeout_sec"]!.GetValue<int>().ShouldBe(30);
    }

    [Fact]
    public void Build_RelativeCommandPath_IsMadeAbsolute()
    {
        var entry = ServerEntryBuilder.Build(
            McpClientRegistry.Get(McpClientId.Claude), "/work/demo", "squeezr", watch: true);

        Command(entry).ShouldBe(Path.GetFullPath("squeezr"));
    }

    [Fact]
    public void Build_ForCurrentProcess_CommandIsAbsolutePath()
    {
        var entry = ServerEntryBuilder.Build(
            McpClientRegistry.Get(McpClientId.Claude), "/work/demo", watch: true);

        var command = Command(entry);
        Path.IsPathRooted(command).ShouldBeTrue();
        Path.GetFileName(command).ShouldNotBeNullOrEmpty();
    }

    private static JsonObject Build(McpClientId clientId, bool watch) =>
        ServerEntryBuilder.Build(McpClientRegistry.Get(clientId), "/work/demo", CommandPath, watch);

    private static string Command(JsonObject entry) => entry["command"]!.GetValue<string>();

    private static string[] Args(JsonObject entry) =>
        entry["args"]!.AsArray().Select(arg => arg!.GetValue<string>()).ToArray();
}
