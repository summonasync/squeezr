using System.Text.Json.Nodes;
using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class McpOnboardingServiceTests
{
    [Fact]
    public async Task Install_KimiProjectScope_WritesEntryWithCwdAndWatchArgs()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var result = await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.Method.ShouldBe("writer");
        result.Error.ShouldBeNull();
        var entry = ReadSqueezrEntry(Path.Combine(workspace, ".kimi-code", "mcp.json"));
        entry["command"]!.GetValue<string>().ShouldBe(Path.GetFullPath(environment.CommandPath));
        entry["args"]!.ToJsonString().ShouldBe("""["serve","--watch"]""");
        entry["cwd"]!.GetValue<string>().ShouldBe(Path.GetFullPath(workspace));
    }

    [Fact]
    public async Task Install_KimiProjectScope_Twice_ProducesIdenticalConfig()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);
        var configPath = Path.Combine(workspace, ".kimi-code", "mcp.json");

        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);
        var first = File.ReadAllText(configPath);
        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        File.ReadAllText(configPath).ShouldBe(first);
    }

    [Fact]
    public async Task Install_CliFirstClientWithCli_UsesCliWithExpectedArguments()
    {
        var (service, environment, _, binDir) = NewService();
        var fake = FakeCli.Install(binDir, "claude");
        var client = McpClientRegistry.Get(McpClientId.Claude);

        var result = await service.InstallAsync(client, McpScope.User, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.Method.ShouldBe("cli");
        fake.ReadCapturedArguments().ShouldBe(
            ["mcp", "add", "squeezr", "-s", "user", "--", environment.CommandPath, "serve", "--watch"]);
    }

    [Fact]
    public async Task Install_CliFirstClientWithoutCli_FallsBackToWriterWithWarning()
    {
        var (service, environment, _, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);

        var result = await service.InstallAsync(client, McpScope.User, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.Method.ShouldBe("writer");
        result.Warning.ShouldNotBeNull().ShouldContain("qwen");
        var entry = ReadSqueezrEntry(Path.Combine(environment.HomeDir, ".qwen", "settings.json"));
        entry["versionNegotiation"]!.GetValue<string>().ShouldBe("auto");
        entry["cwd"]!.GetValue<string>().ShouldBe(Path.GetFullPath(environment.WorkspaceRoot));
    }

    [Fact]
    public async Task Install_CodexProjectScopeWithCliPresent_UsesWriterWithWarning()
    {
        var (service, environment, workspace, binDir) = NewService();
        FakeCli.Install(binDir, "codex");
        var client = McpClientRegistry.Get(McpClientId.Codex);

        var result = await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.Method.ShouldBe("writer");
        result.Warning.ShouldNotBeNull().ShouldContain("project scope");
        var config = File.ReadAllText(Path.Combine(workspace, ".codex", "config.toml"));
        config.ShouldContain("[mcp_servers.squeezr]");
        config.ShouldContain("startup_timeout_sec = 30");
    }

    [Fact]
    public async Task Install_NoWatch_WritesServeArgsWithoutWatchFlag()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        await service.InstallAsync(client, McpScope.Project, watch: false, writeInstructions: false, environment, CancellationToken.None);

        ReadSqueezrEntry(Path.Combine(workspace, ".kimi-code", "mcp.json"))["args"]!.ToJsonString().ShouldBe("""["serve"]""");
    }

    [Fact]
    public async Task Install_WriteInstructions_WritesInstructionBlock()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var result = await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: true, environment, CancellationToken.None);

        result.InstructionsWritten.ShouldBeTrue();
        var instructionPath = Path.Combine(workspace, "AGENTS.md");
        File.ReadAllText(instructionPath).ShouldContain(SqueezrInstructions.StartMarker);
    }

    [Fact]
    public async Task Install_WithoutInstructions_LeavesInstructionFileAbsent()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var result = await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.InstructionsWritten.ShouldBeFalse();
        File.Exists(Path.Combine(workspace, "AGENTS.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task Remove_KimiProjectScope_RemovesEntryAndPreservesOtherServers()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);
        var configPath = Path.Combine(workspace, ".kimi-code", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"mcpServers":{"other":{"command":"other-cmd"}}}""");
        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        await service.RemoveAsync(client, McpScope.Project, environment, CancellationToken.None);

        var root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        root["mcpServers"]!.AsObject().ContainsKey("squeezr").ShouldBeFalse();
        root["mcpServers"]!.AsObject().ContainsKey("other").ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_AfterInstallWithInstructions_RemovesInstructionBlock()
    {
        var (service, environment, workspace, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);
        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: true, environment, CancellationToken.None);
        var instructionPath = Path.Combine(workspace, "AGENTS.md");
        File.Exists(instructionPath).ShouldBeTrue();

        var result = await service.RemoveAsync(client, McpScope.Project, environment, CancellationToken.None);

        result.InstructionsRemoved.ShouldBeTrue();
        File.Exists(instructionPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Doctor_AfterProjectInstall_ReportsEntryIndexingAndSqueezrPath()
    {
        var (service, environment, workspace, binDir) = NewService();
        FakeCli.Install(binDir, "squeezr");
        new WorkspaceManager(workspace).Init("demo");
        File.Exists(SqueezrPaths.MetaDbPath(workspace)).ShouldBeTrue();
        Directory.Exists(Path.Combine(workspace, ".squeezr")).ShouldBeFalse();
        var client = McpClientRegistry.Get(McpClientId.Kimi);
        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.UserScopeEntry.ShouldBeFalse();
        diagnostics.ProjectScopeEntry.ShouldBeTrue();
        diagnostics.ProjectScopeMethod.ShouldBe("writer");
        diagnostics.SqueezrInPath.ShouldBeTrue();
        diagnostics.WorkspaceIndexed.ShouldBeTrue();
        diagnostics.HeadlessStatus.ShouldBeNull();
    }

    [Fact]
    public async Task Doctor_MetaDbOnlyInProjectDirectory_IsNotReportedIndexed()
    {
        var (service, environment, workspace, _) = NewService();
        new WorkspaceManager(workspace).Init("demo");
        File.Delete(SqueezrPaths.MetaDbPath(workspace));
        Directory.CreateDirectory(Path.Combine(workspace, ".squeezr"));
        File.WriteAllText(Path.Combine(workspace, ".squeezr", "meta.db"), string.Empty);
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.WorkspaceIndexed.ShouldBeFalse();
    }

    [Fact]
    public async Task Doctor_ClientCliListsSqueezr_ReportsRegistered()
    {
        var (service, environment, _, binDir) = NewService();
        FakeCli.Install(binDir, "claude", stdout: "squeezr: local index (stdio)\nother: memo");
        var client = McpClientRegistry.Get(McpClientId.Claude);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.Installed.ShouldBeTrue();
        diagnostics.HeadlessStatus.ShouldBe("registered");
    }

    [Fact]
    public async Task Doctor_ClientCliListOmitsSqueezr_ReportsNotRegistered()
    {
        var (service, environment, _, binDir) = NewService();
        FakeCli.Install(binDir, "claude", stdout: "other: memo");
        var client = McpClientRegistry.Get(McpClientId.Claude);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.HeadlessStatus.ShouldBe("not registered");
    }

    [Fact]
    public async Task Doctor_CodexProjectEntry_WarnsAboutTrustedProjects()
    {
        var (service, environment, _, binDir) = NewService();
        FakeCli.Install(binDir, "codex");
        var client = McpClientRegistry.Get(McpClientId.Codex);
        await service.InstallAsync(client, McpScope.Project, watch: true, writeInstructions: false, environment, CancellationToken.None);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.ProjectScopeEntry.ShouldBeTrue();
        diagnostics.Warnings.ShouldContain(warning => warning.Contains("trusted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Install_KimiUserScopeWithHomeOverride_WritesToOverrideDirectory()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), overrideDir);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        await service.InstallAsync(client, McpScope.User, watch: true, writeInstructions: true, environment, CancellationToken.None);

        File.Exists(Path.Combine(overrideDir, "mcp.json")).ShouldBeTrue();
        File.Exists(Path.Combine(overrideDir, "AGENTS.md")).ShouldBeTrue();
        File.Exists(Path.Combine(home, ".kimi-code", "mcp.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_KimiUserScopeWithoutOverride_WritesToDefaultHome()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        await service.InstallAsync(client, McpScope.User, watch: true, writeInstructions: false, environment, CancellationToken.None);

        File.Exists(Path.Combine(home, ".kimi-code", "mcp.json")).ShouldBeTrue();
    }

    private static (McpOnboardingService Service, McpOnboardingEnvironment Environment, string Workspace, string BinDir) NewService()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var binDir = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        return (new McpOnboardingService(new ClientCliLocator(binDir)), environment, workspace, binDir);
    }

    private static JsonObject ReadSqueezrEntry(string configPath) =>
        JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!["squeezr"]!.AsObject();
}
