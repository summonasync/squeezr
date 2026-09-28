using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

[Collection("CliProcessGlobals")]
public sealed class McpProjectBlockCommandsTests
{
    [Fact]
    public async Task Install_ProjectWithoutClients_WritesBlockToAgentsAndQwen_SkipsPointer()
    {
        var repo = NewFixtureRepo();

        var (exitCode, console, _, error) = await RunAsync(repo, ["mcp", "install", "--project"]);

        exitCode.ShouldBe(0);
        error.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldContain(ProjectInstructions.StartMarker);
        File.ReadAllText(Path.Combine(repo, "QWEN.md")).ShouldContain(ProjectInstructions.StartMarker);
        File.ReadAllText(Path.Combine(repo, "CLAUDE.md")).ShouldBe("@AGENTS.md\n");
        console.Output.ShouldContain("project block written");
    }

    [Fact]
    public async Task Install_ProjectTwice_KeepsSingleBlockAndIdenticalContent()
    {
        var repo = NewFixtureRepo();
        await RunAsync(repo, ["mcp", "install", "--project"]);
        var once = File.ReadAllText(Path.Combine(repo, "AGENTS.md"));

        var (exitCode, _, _, _) = await RunAsync(repo, ["mcp", "install", "--project"]);

        exitCode.ShouldBe(0);
        var twice = File.ReadAllText(Path.Combine(repo, "AGENTS.md"));
        twice.ShouldBe(once);
        twice.Split(ProjectInstructions.StartMarker).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Install_PlainWithoutProjectFlag_WritesNoProjectBlock()
    {
        var repo = NewFixtureRepo();

        var (exitCode, _, _, _) = await RunAsync(repo, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes"]);

        exitCode.ShouldBe(0);
        foreach (var file in Directory.GetFiles(repo, "*.md"))
        {
            File.ReadAllText(file).ShouldNotContain(ProjectInstructions.StartMarker);
        }
        File.ReadAllText(Path.Combine(repo, "CLAUDE.md")).ShouldBe("@AGENTS.md\n");
        File.ReadAllText(Path.Combine(repo, "QWEN.md")).ShouldBe("# Qwen Rules\n\nBe terse.\n");
    }

    [Fact]
    public async Task Install_AfterProjectBlock_PlainInstallKeepsBlocksSeparateOnRemove()
    {
        var repo = NewFixtureRepo();
        await RunAsync(repo, ["mcp", "install", "--project"]);
        await RunAsync(repo, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes", "--instructions"]);
        var agents = Path.Combine(repo, "AGENTS.md");
        File.ReadAllText(agents).ShouldContain(ProjectInstructions.StartMarker);
        File.ReadAllText(agents).ShouldContain(SqueezrInstructions.StartMarker);

        await RunAsync(repo, ["mcp", "remove", "--project"]);

        var after = File.ReadAllText(agents);
        after.ShouldNotContain(ProjectInstructions.StartMarker);
        after.ShouldContain(SqueezrInstructions.StartMarker);
    }

    [Fact]
    public async Task Install_ProjectWithClient_RegistersAndWritesBlock()
    {
        var repo = NewFixtureRepo();

        var (exitCode, console, _, _) = await RunAsync(
            repo, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes", "--project"]);

        exitCode.ShouldBe(0);
        File.Exists(Path.Combine(repo, ".kimi-code", "mcp.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldContain(ProjectInstructions.StartMarker);
        console.Output.ShouldContain("project block written");
    }

    [Fact]
    public async Task Install_ProjectOrphanStartMarker_WarnsOnceAndLeavesFileUntouched()
    {
        var repo = NewFixtureRepo();
        var agents = Path.Combine(repo, "AGENTS.md");
        const string orphan = "# Guide\n\n" + ProjectInstructions.StartMarker + "\nquoted start only\n";
        File.WriteAllText(agents, orphan);

        var (exitCode, _, _, error) = await RunAsync(repo, ["mcp", "install", "--project"]);

        exitCode.ShouldBe(0);
        WarningLines(error).ShouldBe(1);
        error.ShouldContain(agents);
        error.ShouldContain("untouched");
        File.ReadAllText(agents).ShouldBe(orphan);
        File.ReadAllText(Path.Combine(repo, "QWEN.md")).ShouldContain(ProjectInstructions.StartMarker);
    }

    [Fact]
    public async Task Install_ProjectOrphanEndMarker_WarnsOnceAndLeavesFileUntouched()
    {
        var repo = NewFixtureRepo();
        var qwen = Path.Combine(repo, "QWEN.md");
        const string orphan = "# Rules\n\ntrailing\n" + ProjectInstructions.EndMarker + "\n";
        File.WriteAllText(qwen, orphan);

        var (exitCode, _, _, error) = await RunAsync(repo, ["mcp", "install", "--project"]);

        exitCode.ShouldBe(0);
        WarningLines(error).ShouldBe(1);
        error.ShouldContain(qwen);
        File.ReadAllText(qwen).ShouldBe(orphan);
        File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldContain(ProjectInstructions.StartMarker);
    }

    [Fact]
    public async Task Remove_ProjectWithoutClients_StripsAllCarriersAndKeepsFiles()
    {
        var repo = NewFixtureRepo();
        await RunAsync(repo, ["mcp", "install", "--project"]);

        var (exitCode, console, _, _) = await RunAsync(repo, ["mcp", "remove", "--project"]);

        exitCode.ShouldBe(0);
        File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldBeEmpty();
        File.ReadAllText(Path.Combine(repo, "QWEN.md")).ShouldBe("# Qwen Rules\n\nBe terse.\n");
        File.ReadAllText(Path.Combine(repo, "CLAUDE.md")).ShouldBe("@AGENTS.md\n");
        console.Output.ShouldContain("project block removed");
    }

    [Fact]
    public async Task Remove_ProjectWithClient_RemovesRegistrationAndBlock()
    {
        var repo = NewFixtureRepo();
        await RunAsync(repo, ["mcp", "install", "--client", "kimi", "--scope", "project", "--yes", "--project"]);

        var (exitCode, _, _, _) = await RunAsync(
            repo, ["mcp", "remove", "--client", "kimi", "--scope", "project", "--project"]);

        exitCode.ShouldBe(0);
        var configPath = Path.Combine(repo, ".kimi-code", "mcp.json");
        JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]!.AsObject()
            .ContainsKey("squeezr").ShouldBeFalse();
        File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldNotContain(ProjectInstructions.StartMarker);
    }

    [Fact]
    public async Task Install_ProjectJson_EmitsMachineReadableProjectEntry()
    {
        var repo = NewFixtureRepo();

        var (exitCode, _, output, _) = await RunAsync(repo, ["mcp", "install", "--project", "--json"]);

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var entry = document.RootElement[0];
        entry.GetProperty("client").GetString().ShouldBe("project");
        entry.GetProperty("scope").GetString().ShouldBe("project");
        entry.GetProperty("method").GetString().ShouldBe("writer");
        entry.GetProperty("instructionsWritten").GetBoolean().ShouldBeTrue();
        entry.TryGetProperty("error", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_InteractivePromptOfferAccepted_WritesProjectBlock()
    {
        var repo = NewFixtureRepo();
        var console = NewInteractiveConsole();
        Push(console, ConsoleKey.Spacebar, ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Y, ConsoleKey.Enter);
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, _, _) = await RunAsync(repo, ["mcp", "install"], console);

            exitCode.ShouldBe(0);
            console.Output.ShouldContain("project block");
            File.ReadAllText(Path.Combine(repo, "AGENTS.md")).ShouldContain(ProjectInstructions.StartMarker);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Install_InteractivePromptDefaultDeclines_LeavesProjectFilesAlone()
    {
        var repo = NewFixtureRepo();
        var console = NewInteractiveConsole();
        Push(console, ConsoleKey.Spacebar, ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter);
        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, _, _) = await RunAsync(repo, ["mcp", "install"], console);

            exitCode.ShouldBe(0);
            File.Exists(Path.Combine(repo, "AGENTS.md")).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    private static int WarningLines(string error) =>
        error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("warning:", StringComparison.Ordinal));

    private static string NewFixtureRepo()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "@AGENTS.md\n");
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "# Qwen Rules\n\nBe terse.\n");
        return root;
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
        TestConsole? console = null)
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
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        AnsiConsole.Console = console;
        Environment.CurrentDirectory = workspace;
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        Environment.SetEnvironmentVariable("HOME", TestDirs.NewTempDir());
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
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }
}
