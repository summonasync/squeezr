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
}
