using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class ClientCliBridgeTests
{
    private const string CommandPath = "/opt/squeezr/bin/squeezr";

    [Fact]
    public async Task AddAsync_ClaudeUserScopeWithWatch_GeneratesExpectedArgv()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        var fake = FakeCli.Install(binDir, "claude");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        await bridge.AddAsync(
            McpClientRegistry.Get(McpClientId.Claude), McpScope.User, CommandPath, watch: true, CancellationToken.None);

        fake.ReadCapturedArguments().ShouldBe(
            ["mcp", "add", "squeezr", "-s", "user", "--", CommandPath, "serve", "--watch"]);
    }

    [Fact]
    public async Task AddAsync_QwenProjectScopeWithoutWatch_GeneratesExpectedArgv()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        var fake = FakeCli.Install(binDir, "qwen");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        await bridge.AddAsync(
            McpClientRegistry.Get(McpClientId.Qwen), McpScope.Project, CommandPath, watch: false, CancellationToken.None);

        fake.ReadCapturedArguments().ShouldBe(
            ["mcp", "add", "squeezr", "-s", "project", "--", CommandPath, "serve"]);
    }

    [Fact]
    public async Task AddAsync_Codex_GeneratesArgvWithoutScope()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        var fake = FakeCli.Install(binDir, "codex");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        await bridge.AddAsync(
            McpClientRegistry.Get(McpClientId.Codex), McpScope.User, CommandPath, watch: true, CancellationToken.None);

        fake.ReadCapturedArguments().ShouldBe(
            ["mcp", "add", "squeezr", "--", CommandPath, "serve", "--watch"]);
    }

    [Fact]
    public async Task RemoveAsync_GeneratesExpectedArgv()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        var fake = FakeCli.Install(binDir, "claude");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        await bridge.RemoveAsync(McpClientRegistry.Get(McpClientId.Claude), CancellationToken.None);

        fake.ReadCapturedArguments().ShouldBe(["mcp", "remove", "squeezr"]);
    }

    [Fact]
    public async Task ListAsync_ReturnsStdout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "qwen", stdout: "squeezr: squeezr serve - OK");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        var output = await bridge.ListAsync(McpClientRegistry.Get(McpClientId.Qwen), CancellationToken.None);

        output.ShouldBe("squeezr: squeezr serve - OK");
    }

    [Fact]
    public async Task AddAsync_NonZeroExit_ThrowsExceptionContainingStderr()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "claude", stderr: "Error: invalid scope", exitCode: 1);
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir));

        var exception = await Should.ThrowAsync<ClientCliException>(() =>
            bridge.AddAsync(McpClientRegistry.Get(McpClientId.Claude), McpScope.User, CommandPath, false, CancellationToken.None));

        exception.ExitCode.ShouldBe(1);
        exception.Stderr.ShouldContain("invalid scope");
        exception.Message.ShouldContain("Error: invalid scope");
    }

    [Fact]
    public async Task AddAsync_Timeout_ThrowsTimeoutException()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "claude", blockingStatement: "exec sleep 30");
        var bridge = new ClientCliBridge(new ClientCliLocator(binDir), TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<TimeoutException>(() =>
            bridge.AddAsync(McpClientRegistry.Get(McpClientId.Claude), McpScope.User, CommandPath, false, CancellationToken.None));
    }

    [Fact]
    public async Task AddAsync_BinaryMissing_ThrowsFileNotFound()
    {
        var bridge = new ClientCliBridge(new ClientCliLocator(string.Empty));

        await Should.ThrowAsync<FileNotFoundException>(() =>
            bridge.AddAsync(McpClientRegistry.Get(McpClientId.Claude), McpScope.User, CommandPath, false, CancellationToken.None));
    }
}
