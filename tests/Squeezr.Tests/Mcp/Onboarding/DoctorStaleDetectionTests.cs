using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class DoctorStaleDetectionTests
{
    [Fact]
    public async Task Doctor_JsonStaleCommand_ReportsUserStaleWithRegisteredAndExpected()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var stalePath = "/opt/old-version/squeezr";
        var configPath = Path.Combine(home, ".kimi-code", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["squeezr"] = new JsonObject
                {
                    ["command"] = stalePath,
                    ["args"] = new JsonArray("serve", "--watch"),
                },
            },
        };
        File.WriteAllText(configPath, json.ToJsonString());
        var environment = new McpOnboardingEnvironment(home, workspace, Environment.ProcessPath!, null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.UserScopeEntry.ShouldBeTrue();
        diagnostics.UserStale.ShouldBeTrue();
        diagnostics.UserCommand.ShouldBe(stalePath);
        diagnostics.ExpectedCommand.ShouldBe(StableCommandPath.Resolve(home, Environment.ProcessPath));
    }

    [Fact]
    public async Task Doctor_TomlStaleCommand_ReportsProjectStale()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var stalePath = "/usr/local/bin/squeezr";
        var configDir = Path.Combine(workspace, ".codex");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "config.toml");
        File.WriteAllText(configPath, $"""
[mcp_servers.squeezr]
command = "{stalePath}"
args = ["serve", "--watch"]
startup_timeout_sec = 30
""");
        var environment = new McpOnboardingEnvironment(home, workspace, Environment.ProcessPath!, null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Codex);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.ProjectScopeEntry.ShouldBeTrue();
        diagnostics.ProjectStale.ShouldBeTrue();
        diagnostics.ProjectCommand.ShouldBe(stalePath);
        diagnostics.ExpectedCommand.ShouldBe(StableCommandPath.Resolve(home, Environment.ProcessPath));
    }

    [Fact]
    public async Task Doctor_BrokenToml_DoesNotCrash()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var configDir = Path.Combine(workspace, ".codex");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "config.toml");
        File.WriteAllText(configPath, "[mcp_servers.squeezr\ncommand = broken");
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Codex);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.ProjectCommand.ShouldBeNull();
        diagnostics.ProjectStale.ShouldBeFalse();
    }

    [Fact]
    public async Task Doctor_CleanCommand_MatchesExpected_NoStale()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var expectedPath = StableCommandPath.Resolve(home, Environment.ProcessPath);
        var configPath = Path.Combine(home, ".kimi-code", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var cleanJson = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["squeezr"] = new JsonObject
                {
                    ["command"] = expectedPath,
                    ["args"] = new JsonArray("serve", "--watch"),
                },
            },
        };
        File.WriteAllText(configPath, cleanJson.ToJsonString());
        var environment = new McpOnboardingEnvironment(home, workspace, expectedPath, null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.UserScopeEntry.ShouldBeTrue();
        diagnostics.UserStale.ShouldBeFalse();
        diagnostics.UserCommand.ShouldBe(expectedPath);
    }

    [Fact]
    public async Task Doctor_MissingConfig_NoStale()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.UserScopeEntry.ShouldBeFalse();
        diagnostics.UserStale.ShouldBeFalse();
        diagnostics.UserCommand.ShouldBeNull();
    }

    [Fact]
    public async Task Doctor_JsonOutput_ContainsNewFieldsAdditive()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var expectedPath = StableCommandPath.Resolve(home, Environment.ProcessPath);
        var configPath = Path.Combine(home, ".kimi-code", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var jsonFixture = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["squeezr"] = new JsonObject
                {
                    ["command"] = expectedPath,
                    ["args"] = new JsonArray("serve", "--watch"),
                },
            },
        };
        File.WriteAllText(configPath, jsonFixture.ToJsonString());
        var environment = new McpOnboardingEnvironment(home, workspace, expectedPath, null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);
        var serialized = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;

        root.TryGetProperty("userCommand", out var userCommandProp).ShouldBeTrue();
        userCommandProp.GetString().ShouldBe(expectedPath);
        root.TryGetProperty("projectCommand", out _).ShouldBeTrue();
        root.TryGetProperty("userStale", out var userStaleProp).ShouldBeTrue();
        userStaleProp.GetBoolean().ShouldBeFalse();
        root.TryGetProperty("projectStale", out _).ShouldBeTrue();
        root.TryGetProperty("expectedCommand", out var expectedProp).ShouldBeTrue();
        expectedProp.GetString().ShouldBe(expectedPath);
        root.TryGetProperty("client", out _).ShouldBeTrue();
        root.TryGetProperty("installed", out _).ShouldBeTrue();
        root.TryGetProperty("userScopeEntry", out _).ShouldBeTrue();
        root.TryGetProperty("projectScopeEntry", out _).ShouldBeTrue();
        root.TryGetProperty("hookStatus", out var hookStatusProp).ShouldBeTrue();
        hookStatusProp.GetString().ShouldBe(HooksBlock.LayoutUnverifiedReason);
    }

    [Fact]
    public async Task Doctor_QwenUserArmed_ReportsArmedState()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Qwen);
        await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.HookStatus.ShouldBe(HooksBlock.ArmedState);
    }

    [Fact]
    public async Task Doctor_QwenProjectArmedOnly_ReportsProjectScopeState()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Qwen);
        await service.InstallAsync(client, McpScope.Project, watch: false, writeInstructions: false, environment, CancellationToken.None);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.HookStatus.ShouldBe(HooksBlock.ProjectArmedState);
    }

    [Fact]
    public async Task Doctor_Codex_ReportsUnverifiedReasonWithoutInventingAPath()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));
        var client = McpClientRegistry.Get(McpClientId.Codex);

        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);

        diagnostics.HookStatus.ShouldBe(HooksBlock.LayoutUnverifiedReason);
        client.GetHooksPath(home, workspace, McpScope.User).ShouldBeNull();
    }

    [Fact]
    public async Task TryGetServerCommand_TomlInlineComment_ReturnsValueWithoutComment()
    {
        var dir = TestDirs.NewTempDir();
        var configPath = Path.Combine(dir, "config.toml");
        File.WriteAllText(configPath, """
[mcp_servers.squeezr]
command = "/opt/squeezr/bin/squeezr" # stable link, do not touch
args = ["serve"]
""");

        var command = await McpConfigProbe.TryGetServerCommandAsync(
            configPath, McpConfigFormat.Toml, "squeezr", CancellationToken.None);

        command.ShouldBe("/opt/squeezr/bin/squeezr");
    }

    [Fact]
    public void PathsMatch_SamePathDifferentSeparators_ReturnsTrue()
    {
        if (OperatingSystem.IsWindows()) return;

        var path1 = "/home/user/.local/bin/squeezr";
        var path2 = "/home/user/.local/bin/squeezr/";

        McpOnboardingService.PathsMatch(path1, path2).ShouldBeTrue();
    }

    [Fact]
    public void PathsMatch_DifferentPaths_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows()) return;

        var path1 = "/home/user/.local/bin/squeezr";
        var path2 = "/opt/squeezr/bin/squeezr";

        McpOnboardingService.PathsMatch(path1, path2).ShouldBeFalse();
    }
}
