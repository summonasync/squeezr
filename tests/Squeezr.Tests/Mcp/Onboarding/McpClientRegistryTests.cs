using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class McpClientRegistryTests
{
    private const string Home = "/home/testhome";
    private const string Root = "/repo";

    [Fact]
    public void All_ContainsExactlyFourClients()
    {
        McpClientRegistry.All.Count.ShouldBe(4);
        McpClientRegistry.All.Select(c => c.Id).ShouldBe(
            [McpClientId.Claude, McpClientId.Kimi, McpClientId.Qwen, McpClientId.Codex]);
    }

    [Theory]
    [InlineData(McpClientId.Claude, "Claude Code", "claude")]
    [InlineData(McpClientId.Kimi, "Kimi Code", "kimi")]
    [InlineData(McpClientId.Qwen, "Qwen Code", "qwen")]
    [InlineData(McpClientId.Codex, "Codex CLI", "codex")]
    public void Get_KnownId_ReturnsExpectedFacts(McpClientId id, string displayName, string binaryName)
    {
        var descriptor = McpClientRegistry.Get(id);

        descriptor.Id.ShouldBe(id);
        descriptor.DisplayName.ShouldBe(displayName);
        descriptor.BinaryName.ShouldBe(binaryName);
    }

    [Theory]
    [InlineData("claude", McpClientId.Claude)]
    [InlineData("Claude-Code", McpClientId.Claude)]
    [InlineData("KIMI", McpClientId.Kimi)]
    [InlineData("kimi-code", McpClientId.Kimi)]
    [InlineData("qwen", McpClientId.Qwen)]
    [InlineData("qwen-code", McpClientId.Qwen)]
    [InlineData("codex", McpClientId.Codex)]
    [InlineData("codex-cli", McpClientId.Codex)]
    public void TryGet_KnownName_ReturnsDescriptor(string name, McpClientId expectedId)
    {
        var found = McpClientRegistry.TryGet(name, out var descriptor);

        found.ShouldBeTrue();
        descriptor.Id.ShouldBe(expectedId);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("")]
    [InlineData("claude code")]
    public void TryGet_UnknownName_ReturnsFalse(string name)
    {
        McpClientRegistry.TryGet(name, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(McpClientId.Claude, ".claude.json", ".mcp.json")]
    [InlineData(McpClientId.Kimi, ".kimi-code/mcp.json", ".kimi-code/mcp.json")]
    [InlineData(McpClientId.Qwen, ".qwen/settings.json", ".qwen/settings.json")]
    [InlineData(McpClientId.Codex, ".codex/config.toml", ".codex/config.toml")]
    public void GetConfigPath_ReturnsScopeSpecificPath(McpClientId id, string userRelative, string projectRelative)
    {
        var descriptor = McpClientRegistry.Get(id);

        descriptor.GetConfigPath(Home, Root, McpScope.User)
            .ShouldBe(Path.Combine([Home, .. userRelative.Split('/')]));
        descriptor.GetConfigPath(Home, Root, McpScope.Project)
            .ShouldBe(Path.Combine([Root, .. projectRelative.Split('/')]));
    }

    [Theory]
    [InlineData(McpClientId.Claude, ".claude/CLAUDE.md", "CLAUDE.md")]
    [InlineData(McpClientId.Kimi, ".kimi-code/AGENTS.md", "AGENTS.md")]
    [InlineData(McpClientId.Qwen, ".qwen/QWEN.md", "QWEN.md")]
    [InlineData(McpClientId.Codex, ".codex/AGENTS.md", "AGENTS.md")]
    public void GetInstructionPath_ReturnsScopeSpecificPath(McpClientId id, string userRelative, string projectRelative)
    {
        var descriptor = McpClientRegistry.Get(id);

        descriptor.GetInstructionPath(Home, Root, McpScope.User)
            .ShouldBe(Path.Combine([Home, .. userRelative.Split('/')]));
        descriptor.GetInstructionPath(Home, Root, McpScope.Project)
            .ShouldBe(Path.Combine([Root, .. projectRelative.Split('/')]));
    }

    [Theory]
    [InlineData(McpClientId.Claude, false)]
    [InlineData(McpClientId.Kimi, true)]
    [InlineData(McpClientId.Qwen, true)]
    [InlineData(McpClientId.Codex, true)]
    public void SupportsCurrentDirectory_MatchesClientFacts(McpClientId id, bool expected)
    {
        McpClientRegistry.Get(id).SupportsCurrentDirectory.ShouldBe(expected);
    }

    [Fact]
    public void ConfigFormat_QwenAndKimiAndClaudeAreJson_CodexIsToml()
    {
        McpClientRegistry.Get(McpClientId.Claude).ConfigFormat.ShouldBe(McpConfigFormat.Json);
        McpClientRegistry.Get(McpClientId.Kimi).ConfigFormat.ShouldBe(McpConfigFormat.Json);
        McpClientRegistry.Get(McpClientId.Qwen).ConfigFormat.ShouldBe(McpConfigFormat.Json);
        McpClientRegistry.Get(McpClientId.Codex).ConfigFormat.ShouldBe(McpConfigFormat.Toml);
    }

    [Fact]
    public void WriteStrategy_KimiIsWriterOnly_OthersAreCliFirst()
    {
        McpClientRegistry.Get(McpClientId.Kimi).WriteStrategy.ShouldBe(McpWriteStrategy.WriterOnly);
        McpClientRegistry.Get(McpClientId.Claude).WriteStrategy.ShouldBe(McpWriteStrategy.CliFirst);
        McpClientRegistry.Get(McpClientId.Qwen).WriteStrategy.ShouldBe(McpWriteStrategy.CliFirst);
        McpClientRegistry.Get(McpClientId.Codex).WriteStrategy.ShouldBe(McpWriteStrategy.CliFirst);
    }

    [Fact]
    public void Qwen_RequiresVersionNegotiationAuto()
    {
        McpClientRegistry.Get(McpClientId.Qwen).RequiredVersionNegotiation.ShouldBe("auto");
        McpClientRegistry.Get(McpClientId.Claude).RequiredVersionNegotiation.ShouldBeNull();
    }

    [Fact]
    public void Codex_StartupTimeoutSecondsIs30_OthersUnset()
    {
        McpClientRegistry.Get(McpClientId.Codex).StartupTimeoutSeconds.ShouldBe(30);
        McpClientRegistry.Get(McpClientId.Claude).StartupTimeoutSeconds.ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Kimi).StartupTimeoutSeconds.ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Qwen).StartupTimeoutSeconds.ShouldBeNull();
    }

    [Fact]
    public void Kimi_HomeEnvironmentVariableIsKimiCodeHome_OthersUnset()
    {
        McpClientRegistry.Get(McpClientId.Kimi).HomeEnvironmentVariable.ShouldBe("KIMI_CODE_HOME");
        McpClientRegistry.Get(McpClientId.Claude).HomeEnvironmentVariable.ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Qwen).HomeEnvironmentVariable.ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Codex).HomeEnvironmentVariable.ShouldBeNull();
    }
}
