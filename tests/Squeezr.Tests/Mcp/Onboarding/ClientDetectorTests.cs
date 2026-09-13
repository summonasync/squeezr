using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class ClientDetectorTests
{
    [Fact]
    public void IsInstalled_BinaryInPath_Detected()
    {
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "kimi");
        var environment = NewEnvironment(TestDirs.NewTempDir(), TestDirs.NewTempDir());

        var installed = ClientDetector.IsInstalled(
            McpClientRegistry.Get(McpClientId.Kimi), environment, new ClientCliLocator(binDir));

        installed.ShouldBeTrue();
    }

    [Fact]
    public void IsInstalled_ClaudeUserConfigFileExists_Detected()
    {
        var home = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{}");
        var environment = NewEnvironment(home, TestDirs.NewTempDir());

        var installed = ClientDetector.IsInstalled(
            McpClientRegistry.Get(McpClientId.Claude), environment, new ClientCliLocator(string.Empty));

        installed.ShouldBeTrue();
    }

    [Fact]
    public void IsInstalled_KimiConfigDirectoryExists_Detected()
    {
        var home = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(home, ".kimi-code"));
        var environment = NewEnvironment(home, TestDirs.NewTempDir());

        var installed = ClientDetector.IsInstalled(
            McpClientRegistry.Get(McpClientId.Kimi), environment, new ClientCliLocator(string.Empty));

        installed.ShouldBeTrue();
    }

    [Fact]
    public void IsInstalled_KimiHomeOverrideDirectoryExists_Detected()
    {
        var home = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var environment = NewEnvironment(home, TestDirs.NewTempDir(), overrideDir);

        var installed = ClientDetector.IsInstalled(
            McpClientRegistry.Get(McpClientId.Kimi), environment, new ClientCliLocator(string.Empty));

        installed.ShouldBeTrue();
    }

    [Fact]
    public void IsInstalled_NothingPresent_NotDetected()
    {
        var environment = NewEnvironment(TestDirs.NewTempDir(), TestDirs.NewTempDir());

        foreach (var client in McpClientRegistry.All)
        {
            ClientDetector.IsInstalled(client, environment, new ClientCliLocator(string.Empty)).ShouldBeFalse();
        }
    }

    [Fact]
    public void IsInstalled_HomeDirectoryAloneDoesNotCount_NotDetected()
    {
        var home = TestDirs.NewTempDir();
        var environment = NewEnvironment(home, TestDirs.NewTempDir());

        var installed = ClientDetector.IsInstalled(
            McpClientRegistry.Get(McpClientId.Claude), environment, new ClientCliLocator(string.Empty));

        installed.ShouldBeFalse();
    }

    private static McpOnboardingEnvironment NewEnvironment(string home, string workspace, string? homeOverride = null) =>
        new(home, workspace, Path.Combine(home, "bin", "squeezr"), homeOverride);
}
