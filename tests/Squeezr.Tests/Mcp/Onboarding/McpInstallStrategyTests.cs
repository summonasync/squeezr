using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class McpInstallStrategyTests
{
    [Fact]
    public void Resolve_Kimi_UsesWriterEvenWhenBinaryPresent()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "kimi");
        var locator = new ClientCliLocator(binDir);

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Kimi), McpScope.User, locator);

        decision.Method.ShouldBe(McpInstallMethod.Writer);
    }

    [Fact]
    public void Resolve_CodexProjectScope_UsesWriterEvenWhenBinaryPresent()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "codex");
        var locator = new ClientCliLocator(binDir);

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Codex), McpScope.Project, locator);

        decision.Method.ShouldBe(McpInstallMethod.Writer);
        decision.Warning.ShouldNotBeNull();
    }

    [Fact]
    public void Resolve_CodexUserScope_UsesCliWhenBinaryPresent()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "codex");
        var locator = new ClientCliLocator(binDir);

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Codex), McpScope.User, locator);

        decision.Method.ShouldBe(McpInstallMethod.Cli);
    }

    [Fact]
    public void Resolve_CliFirstClientWithBinaryPresent_UsesCli()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "claude");
        var locator = new ClientCliLocator(binDir);

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Claude), McpScope.User, locator);

        decision.Method.ShouldBe(McpInstallMethod.Cli);
        decision.Warning.ShouldBeNull();
    }

    [Fact]
    public void Resolve_CliFirstClientWithoutBinary_FallsBackToWriterWithWarning()
    {
        var locator = new ClientCliLocator(string.Empty);

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Qwen), McpScope.Project, locator);

        decision.Method.ShouldBe(McpInstallMethod.Writer);
        decision.Warning.ShouldNotBeNull().ShouldContain("qwen");
    }

    [Theory]
    [InlineData(McpClientId.Claude, ".claude/settings.json", ".claude/settings.json")]
    [InlineData(McpClientId.Qwen, ".qwen/settings.json", ".qwen/settings.json")]
    public void GetHooksPath_ArmedClients_ReturnsScopeSpecificPath(
        McpClientId id, string userRelative, string projectRelative)
    {
        var descriptor = McpClientRegistry.Get(id);

        descriptor.GetHooksPath("/home/testhome", "/repo", McpScope.User)
            .ShouldBe(Path.Combine(["/home/testhome", .. userRelative.Split('/')]));
        descriptor.GetHooksPath("/home/testhome", "/repo", McpScope.Project)
            .ShouldBe(Path.Combine(["/repo", .. projectRelative.Split('/')]));
    }

    [Fact]
    public void GetHooksPath_UnverifiedClients_ReturnNullInEveryScope()
    {
        McpClientRegistry.Get(McpClientId.Kimi).GetHooksPath("/home/testhome", "/repo", McpScope.User).ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Kimi).GetHooksPath("/home/testhome", "/repo", McpScope.Project).ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Codex).GetHooksPath("/home/testhome", "/repo", McpScope.User).ShouldBeNull();
        McpClientRegistry.Get(McpClientId.Codex).GetHooksPath("/home/testhome", "/repo", McpScope.Project).ShouldBeNull();
    }

    [Fact]
    public void Resolve_KimiUserScope_HooksUnarmedBecauseLayoutUnverified()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "kimi");

        var decision = McpInstallStrategy.Resolve(McpClientRegistry.Get(McpClientId.Kimi), McpScope.User, new ClientCliLocator(binDir));

        decision.Method.ShouldBe(McpInstallMethod.Writer);
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Kimi), "/opt/squeezr/squeezr").ShouldBeNull();
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Codex), "/opt/squeezr/squeezr").ShouldBeNull();
    }
}
