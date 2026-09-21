using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp.Onboarding;

[Collection("ProcessGlobals")]
public sealed class McpCommandsTests
{
    [Fact]
    public async Task Help_ListAllMcpCommands()
    {
        var (exitCode, console, output, _) = await RunAsync(TestDirs.NewTempDir(), ["mcp", "--help"]);

        exitCode.ShouldBe(0);
        var combined = console.Output + output;
        combined.ShouldContain("install");
        combined.ShouldContain("remove");
        combined.ShouldContain("doctor");
    }

    [Fact]
    public async Task Help_InstallListsAllOptions()
    {
        var (exitCode, console, output, _) = await RunAsync(TestDirs.NewTempDir(), ["mcp", "install", "--help"]);

        exitCode.ShouldBe(0);
        var combined = console.Output + output;
        combined.ShouldContain("--client");
        combined.ShouldContain("--all");
        combined.ShouldContain("--scope");
        combined.ShouldContain("--no-watch");
        combined.ShouldContain("--no-instructions");
        combined.ShouldContain("--yes");
        combined.ShouldContain("--json");
    }

    [Fact]
    public async Task Install_NonInteractiveWithoutClient_ExitsWithHint()
    {
        var workspace = TestDirs.NewTempDir();
        var (exitCode, _, _, error) = await RunAsync(workspace, ["mcp", "install", "--scope", "project", "--yes"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("--client");
        error.ShouldContain("--all");
    }

    [Fact]
    public async Task Install_NonInteractiveYesWithoutScope_DefaultsToUserAndPrintsDecision()
    {
        var workspace = TestDirs.NewTempDir();
        string? homeDir = null;

        var (exitCode, console, _, error) = await RunAsync(
            workspace,
            ["mcp", "install", "--client", "kimi", "--yes"],
            configureHome: home => homeDir = home);

        exitCode.ShouldBe(0);
        console.Output.ShouldContain("scope: user (--yes default");
        error.ShouldBeEmpty();
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(homeDir!, ".kimi-code", "mcp.json")))!["mcpServers"]!["squeezr"]!;
        entry["args"]!.ToJsonString().ShouldBe("""["serve","--watch"]""");
    }

    [Fact]
    public async Task Install_NonInteractiveWithoutYesOrScope_ExitsWithHint()
    {
        var workspace = TestDirs.NewTempDir();
        var (exitCode, _, _, error) = await RunAsync(workspace, ["mcp", "install", "--client", "kimi"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("--scope");
    }

    [Fact]
    public async Task Install_InvalidScope_ExitsWithValidationError()
    {
        var workspace = TestDirs.NewTempDir();
        var (exitCode, _, _, error) = await RunAsync(
            workspace, ["mcp", "install", "--client", "kimi", "--scope", "team", "--yes"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("Invalid --scope");
        error.ShouldContain("'team'");
    }

    [Fact]
    public async Task Install_AllScopeUserYes_InstallsDetectedClientsWithoutPrompts()
    {
        var workspace = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        string? homeDir = null;
        try
        {
            var (exitCode, console, _, _) = await RunAsync(
                workspace,
                ["mcp", "install", "--all", "--scope", "user", "--yes"],
                configureHome: home =>
                {
                    homeDir = home;
                    File.WriteAllText(Path.Combine(home, ".claude.json"), "{}");
                });

            exitCode.ShouldBe(0);
            console.Output.ShouldContain("detected: claude, kimi");
            var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(homeDir!, ".claude.json")))!["mcpServers"]!["squeezr"]!;
            claude["args"]!.ToJsonString().ShouldBe("""["serve","--watch"]""");
            JsonNode.Parse(File.ReadAllText(Path.Combine(overrideDir, "mcp.json")))!["mcpServers"]!["squeezr"]!["args"]!
                .ToJsonString().ShouldBe("""["serve","--watch"]""");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_NonInteractiveUnknownClient_ExitsWithHint()
    {
        var workspace = TestDirs.NewTempDir();
        var (exitCode, _, _, error) = await RunAsync(workspace, ["mcp", "install", "--client", "nope", "--scope", "project"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("Unknown client");
    }

    [Fact]
    public async Task Install_NonInteractiveKimiProject_InstallsAndReports()
    {
        var workspace = TestDirs.NewTempDir();

        var (exitCode, console, _, _) = await RunAsync(workspace, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes"]);

        exitCode.ShouldBe(0);
        console.Output.ShouldContain("kimi");
        var configPath = Path.Combine(workspace, ".kimi-code", "mcp.json");
        File.Exists(configPath).ShouldBeTrue();
        var entry = JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!["squeezr"]!;
        entry["args"]!.ToJsonString().ShouldBe("""["serve","--watch"]""");
        entry.AsObject().Select(static property => property.Key).Order().ShouldBe(["args", "command"]);
        File.ReadAllText(Path.Combine(workspace, "AGENTS.md")).ShouldContain("squeezr:instructions");
    }

    [Fact]
    public async Task Install_FromWorkspaceSubdirectory_WritesEntryWithoutCwd()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var nested = Directory.CreateDirectory(Path.Combine(workspace, "sub")).FullName;

        var (exitCode, _, _, _) = await RunAsync(nested, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes"]);

        exitCode.ShouldBe(0);
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, ".kimi-code", "mcp.json")))!["mcpServers"]!["squeezr"]!;
        entry.AsObject().ContainsKey("cwd").ShouldBeFalse();
    }

    [Fact]
    public async Task Install_NonInteractiveKimiProjectTwice_IsIdempotent()
    {
        var workspace = TestDirs.NewTempDir();
        var args = new[] { "mcp", "install", "--client", "kimi", "--scope", "project", "--yes" };

        await RunAsync(workspace, args);
        var (exitCode, _, output, _) = await RunAsync(workspace, args);

        exitCode.ShouldBe(0);
        var configPath = Path.Combine(workspace, ".kimi-code", "mcp.json");
        JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!.AsObject().Count.ShouldBe(1);
        output.ShouldNotContain("error");
    }

    [Fact]
    public async Task Install_Json_ReturnsMachineReportPerClient()
    {
        var workspace = TestDirs.NewTempDir();

        var (exitCode, _, output, _) = await RunAsync(
            workspace, ["mcp", "install", "--client", "kimi,qwen", "--scope", "project", "--yes", "--json"]);

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var results = document.RootElement;
        results.GetArrayLength().ShouldBe(2);
        var clients = results.EnumerateArray().Select(element => element.GetProperty("client").GetString()).Order().ToArray();
        clients.ShouldBe(["kimi", "qwen"]);
        var kimi = results.EnumerateArray().Single(element => element.GetProperty("client").GetString() == "kimi");
        kimi.GetProperty("scope").GetString().ShouldBe("project");
        kimi.GetProperty("method").GetString().ShouldBe("writer");
        kimi.GetProperty("instructionsWritten").GetBoolean().ShouldBeTrue();
        kimi.TryGetProperty("error", out var error).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_All_InstallsOnlyDetectedClients()
    {
        var workspace = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, output, _) = await RunAsync(
                workspace,
                ["mcp", "install", "--client", "all", "--scope", "project", "--yes", "--json"],
                configureHome: home => File.WriteAllText(Path.Combine(home, ".claude.json"), "{}"));

            exitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(output);
            var clients = document.RootElement.EnumerateArray()
                .Select(element => element.GetProperty("client").GetString())
                .Order()
                .ToArray();
            clients.ShouldBe(["claude", "kimi"]);
            File.Exists(Path.Combine(workspace, ".mcp.json")).ShouldBeTrue();
            File.Exists(Path.Combine(workspace, ".kimi-code", "mcp.json")).ShouldBeTrue();
            Directory.Exists(Path.Combine(workspace, ".qwen")).ShouldBeFalse();
            Directory.Exists(Path.Combine(workspace, ".codex")).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_All_HumanOutputMentionsDetectedClients()
    {
        var workspace = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, console, _, _) = await RunAsync(
                workspace,
                ["mcp", "install", "--client", "all", "--scope", "project", "--yes"],
                configureHome: home => File.WriteAllText(Path.Combine(home, ".claude.json"), "{}"));

            exitCode.ShouldBe(0);
            console.Output.ShouldContain("detected: claude, kimi");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_AllWithoutDetections_ExitsWithError()
    {
        var workspace = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", null);
        try
        {
            var (exitCode, _, _, error) = await RunAsync(
                workspace, ["mcp", "install", "--client", "all", "--scope", "project", "--yes"]);

            exitCode.ShouldBe(1);
            error.ShouldContain("No agent CLIs were detected");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_InteractiveWithoutFlags_PromptsClientsScopeAndInstructions()
    {
        var workspace = TestDirs.NewTempDir();
        var console = NewInteractiveConsole();
        Push(console, ConsoleKey.Spacebar, ConsoleKey.Enter, ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter);
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, output, _) = await RunAsync(workspace, ["mcp", "install", "--json"], console);

            exitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(output);
            var results = document.RootElement;
            results.GetArrayLength().ShouldBe(1);
            results[0].GetProperty("client").GetString().ShouldBe("kimi");
            results[0].GetProperty("scope").GetString().ShouldBe("project");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_InteractiveWithClientFlag_PromptsScopeOnly()
    {
        var workspace = TestDirs.NewTempDir();
        var console = NewInteractiveConsole();
        Push(console, ConsoleKey.DownArrow, ConsoleKey.Enter);

        var (exitCode, _, _, _) = await RunAsync(workspace, ["mcp", "install", "--client", "kimi", "--yes"], console);

        exitCode.ShouldBe(0);
        File.Exists(Path.Combine(workspace, ".kimi-code", "mcp.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(workspace, "AGENTS.md")).ShouldContain("squeezr:instructions");
    }

    [Fact]
    public async Task Remove_NonInteractive_RemovesEntryAndKeepsFile()
    {
        var workspace = TestDirs.NewTempDir();
        await RunAsync(workspace, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes"]);

        var (exitCode, console, _, _) = await RunAsync(workspace, ["mcp", "remove", "--client", "kimi", "--scope", "project"]);

        exitCode.ShouldBe(0);
        console.Output.ShouldContain("removed");
        var configPath = Path.Combine(workspace, ".kimi-code", "mcp.json");
        File.Exists(configPath).ShouldBeTrue();
        JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!.AsObject().ContainsKey("squeezr").ShouldBeFalse();
        File.Exists(Path.Combine(workspace, "AGENTS.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task Doctor_NonInteractiveAfterInstallAndRemove_ExitCodesFollowEntry()
    {
        var workspace = TestDirs.NewTempDir();
        var doctorArgs = new[] { "mcp", "doctor", "--client", "kimi" };

        await RunAsync(workspace, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes"]);
        var (afterInstall, console, _, _) = await RunAsync(workspace, doctorArgs);
        var (afterRemove, _, _, _) = await RunAsync(workspace, ["mcp", "remove", "--client", "kimi", "--scope", "project"]);
        var (finalCode, finalConsole, _, _) = await RunAsync(workspace, doctorArgs);

        afterInstall.ShouldBe(0);
        console.Output.ShouldContain("entry:");
        afterRemove.ShouldBe(0);
        finalCode.ShouldBe(1);
        finalConsole.Output.ShouldContain("entry:");
    }

    [Fact]
    public async Task Install_KimiUserScopeHonorsKimiCodeHomeOverride()
    {
        var workspace = TestDirs.NewTempDir();
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, _, _) = await RunAsync(
                workspace, ["mcp", "install", "--client", "kimi", "--scope", "user", "--yes"]);

            exitCode.ShouldBe(0);
            var configPath = Path.Combine(overrideDir, "mcp.json");
            File.Exists(configPath).ShouldBeTrue();
            JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!.AsObject().ContainsKey("squeezr").ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    private static TestConsole NewInteractiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        return console;
    }

    private static void Push(TestConsole console, params ConsoleKey[] keys)
    {
        foreach (var key in keys)
        {
            console.Input.PushKey(key);
        }
    }

    private static async Task<(int ExitCode, TestConsole Console, string Output, string Error)> RunAsync(
        string workspace,
        string[] args,
        TestConsole? console = null,
        Action<string>? configureHome = null)
    {
        var ownsConsole = console is null;
        console ??= new TestConsole();
        if (ownsConsole)
        {
            console.Profile.Capabilities.Interactive = false;
        }
        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var previousConsole = AnsiConsole.Console;
        var previousDirectory = Environment.CurrentDirectory;
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        // Redirecting HOME alone leaves the real profile in play on Windows, where
        // GetFolderPath(UserProfile) ignores the environment entirely.
        using var environment = new TestEnvironmentScope();
        AnsiConsole.Console = console;
        Environment.CurrentDirectory = workspace;
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        configureHome?.Invoke(environment.HomeDir);
        Console.SetOut(outputWriter);
        Console.SetError(errorWriter);
        try
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.Settings.Console = console;
                McpCommandRegistration.AddMcpBranch(config);
            });
            var exitCode = await app.RunAsync(args);
            return (exitCode, console, outputWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            AnsiConsole.Console = previousConsole;
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }
}
