using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

// The opt-in door for the SessionStart hook, measured with a pinned scratch client home: what the flag writes,
// what its absence refuses to write, and whether remove can take the block back. The pins here exist because
// the alternative is a hook that writes itself into a user's settings file on someone else's initiative.
[Collection("ProcessGlobals")]
public sealed class SessionStartHookInstallTests
{
    [Fact]
    public async Task Install_WithSessionStartFlag_WritesThatBlockAndNothingElse()
    {
        var workspace = TestDirs.NewTempDir();
        var withoutFlag = TestDirs.NewTempDir();
        var withFlag = TestDirs.NewTempDir();

        await RunInstallAsync(workspace, withoutFlag, ["mcp", "install", "--client", "qwen", "--yes", "--json"]);
        var baselineSettings = File.ReadAllText(Path.Combine(withoutFlag, ".qwen", "settings.json"));

        await RunInstallAsync(workspace, withFlag, ["mcp", "install", "--client", "qwen", "--yes", "--json"]);
        await RunInstallAsync(
            workspace, withFlag, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);

        var settingsPath = Path.Combine(withFlag, ".qwen", "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var entry = root["hooks"]!.AsObject()["SessionStart"]!.AsArray()[0]!.AsObject();
        entry["matcher"]!.GetValue<string>().ShouldBe("startup");
        var hook = entry["hooks"]!.AsArray()[0]!.AsObject();
        hook["type"]!.GetValue<string>().ShouldBe("command");
        hook["command"]!.GetValue<string>().ShouldEndWith("hook session-start");
        hook["timeout"]!.GetValue<int>().ShouldBe(HooksBlock.SessionStartTimeoutSeconds);

        // everything the flag did NOT ask for is byte-equal to the arm that never asked it
        root["hooks"]!.AsObject().Remove("SessionStart");
        root.ToJsonString().ShouldBe(JsonNode.Parse(baselineSettings)!.AsObject().ToJsonString());

        // on disk the two arms own the same files: the settings document and the backup its own first write
        // rotated. Nothing about the flag reaches outside that pair.
        FilesIn(withFlag).ShouldBe(FilesIn(withoutFlag),
            "the flag adds a block to the client settings file and its rotated backup, and no other path");
    }

    [Fact]
    public async Task Install_WithoutTheFlag_WritesNoSessionStartBlockAndStillArmsPreRead()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var (exitCode, output, error) = await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "qwen", "--yes", "--json"]);

        exitCode.ShouldBe(0);
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".qwen", "settings.json")))!.AsObject();
        root["hooks"]!.AsObject().ContainsKey("SessionStart").ShouldBeFalse();
        root["hooks"]!.AsObject()["PreToolUse"]!.AsArray().Count.ShouldBe(1);
        JsonDocument.Parse(output.Trim()).RootElement.EnumerateArray()
            .Any(row => row.TryGetProperty("sessionStart", out _)).ShouldBeFalse(
                "an unarmed opt-in is an absent key, not a claim of false");
        error.ShouldContain("session-start hook not armed");
    }

    [Fact]
    public async Task Install_SessionStartFlagTwice_SecondPassLeavesTheFileByteIdentical()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);
        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        var first = File.ReadAllText(settingsPath);

        await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);

        File.ReadAllText(settingsPath).ShouldBe(first);
        JsonNode.Parse(first)!.AsObject()["hooks"]!["SessionStart"]!.AsArray().Count.ShouldBe(1,
            "a re-run adds a second hook object only if the block identity stops working");
    }

    [Fact]
    public async Task Install_ClaudeWithTheFlag_LandsInClaudeSettingsNotTheServerFile()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "claude", "--yes", "--session-start", "--json"]);

        var settings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(home, ".claude", "settings.json")))!.AsObject();
        settings["hooks"]!["SessionStart"]!.AsArray()[0]!["matcher"]!.GetValue<string>().ShouldBe("startup");
        var serverFile = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".claude.json")))!.AsObject();
        serverFile.ContainsKey("hooks").ShouldBeFalse(
            "Claude keeps the server entry in ~/.claude.json and the hooks in ~/.claude/settings.json");
    }

    [Fact]
    public async Task Install_KimiWithTheFlag_SaysItArmedNothingAndWritesNoHookFile()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var (exitCode, _, _) = await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "kimi", "--yes", "--session-start", "--json"]);

        exitCode.ShouldBe(0);
        File.Exists(Path.Combine(home, ".kimi-code", "settings.json")).ShouldBeFalse();
        FilesIn(home).ShouldBe([Path.Combine(".kimi-code", "mcp.json")],
            "a client whose hook layout nobody measured gets a warning, not an invented file");
    }

    [Fact]
    public async Task Remove_AfterTheFlagStripsTheBlockAndLeavesNothingManagedBehind()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);

        // remove takes the scope explicitly — unlike install it has no --yes default, so a stripped block is
        // named by the scope it was armed in rather than assumed
        var (_, _, error) = await RunInstallAsync(
            workspace, home, ["mcp", "remove", "--client", "qwen", "--scope", "user", "--yes", "--json"]);

        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var eventsLeft = root["hooks"]?.AsObject().Select(pair => pair.Key).ToArray() ?? [];
        eventsLeft.ShouldBe([], "remove takes both managed blocks back and leaves no empty hooks key behind");
        error.ShouldNotContain("session-start hook not armed");
    }

    [Fact]
    public async Task Doctor_SeparatesTheTwoHookStatesInJson()
    {
        var workspace = TestDirs.NewTempDir();
        var armed = TestDirs.NewTempDir();
        var notArmed = TestDirs.NewTempDir();
        await RunInstallAsync(
            workspace, armed, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);
        await RunInstallAsync(workspace, notArmed, ["mcp", "install", "--client", "qwen", "--yes", "--json"]);

        var armedReport = await RunInstallAsync(workspace, armed, ["mcp", "doctor", "--client", "qwen", "--json"]);
        var quietReport = await RunInstallAsync(workspace, notArmed, ["mcp", "doctor", "--client", "qwen", "--json"]);

        EntryOf(armedReport.Output).GetProperty("hookStatus").GetString().ShouldBe(HooksBlock.ArmedState);
        EntryOf(armedReport.Output).GetProperty("sessionStartStatus").GetString().ShouldBe(HooksBlock.ArmedState);
        EntryOf(quietReport.Output).GetProperty("hookStatus").GetString().ShouldBe(HooksBlock.ArmedState);
        EntryOf(quietReport.Output).GetProperty("sessionStartStatus").GetString().ShouldBe(HooksBlock.NotArmedState,
            "the pre-read arm says nothing about the opt-in block; a shared state string would lie about both");
    }

    [Fact]
    public async Task Install_ProjectOnlyWithTheFlag_RefusesInsteadOfQuietlyArmingNothing()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var (exitCode, _, error) = await RunInstallAsync(
            workspace, home, ["mcp", "install", "--project", "--session-start", "--yes"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("--session-start arms a client hook and this path arms none");
        FilesIn(home).ShouldBe([], "the refusal happens before any write");
    }

    [Fact]
    public async Task Install_AtATerminal_DoesNotAskForTheArmOnTopOfTheShippedPrompts()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        // the keystrokes the shipped interactive contract already answers with — clients, scope, instructions,
        // project block. A fifth prompt for the opt-in would consume one of these and move those pins, which is
        // exactly why the arm is reachable only through the flag.
        for (var prompt = 0; prompt < 5; prompt++)
        {
            console.Input.PushKey(prompt == 0 ? ConsoleKey.Spacebar : ConsoleKey.Enter);
        }

        var overrideDir = TestDirs.NewTempDir();
        var previousOverride = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", overrideDir);
        try
        {
            var (exitCode, _, _) = await RunInstallAsync(
                workspace, home, ["mcp", "install"], console);

            exitCode.ShouldBe(0);
            console.Output.ShouldNotContain("Arm the SessionStart");
            File.Exists(Path.Combine(overrideDir, "mcp.json")).ShouldBeTrue(
                "the flow the shipped prompts drive still completes on these keystrokes");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousOverride);
        }
    }

    [Fact]
    public async Task Snippet_PrintsTheVeryBlockTheFlagWritesAndCreatesNoFile()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var (printExit, printOutput, _) = await RunInstallAsync(
            workspace, home, ["hook", "snippet", "--session-start", "--client", "qwen", "--json"]);

        printExit.ShouldBe(0);
        FilesIn(home).ShouldBe([], "the printer advertises the block, it does not install it");
        var printed = JsonDocument.Parse(printOutput.Trim()).RootElement
            .EnumerateArray().Single().GetProperty("fragment").GetProperty("hooks").GetProperty("SessionStart");

        await RunInstallAsync(
            workspace, home, ["mcp", "install", "--client", "qwen", "--yes", "--session-start", "--json"]);
        var installed = JsonNode.Parse(
            File.ReadAllText(Path.Combine(home, ".qwen", "settings.json")))!["hooks"]!["SessionStart"]!;

        // the README promises the pasted block and the written block are one thing; this is where that is checked
        JsonNode.Parse(printed.GetRawText())!.ToJsonString().ShouldBe(installed.ToJsonString());
    }

    [Fact]
    public async Task Snippet_KimiAndCodex_HaveNoLayoutToPrint()
    {
        var workspace = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var (exitCode, _, error) = await RunInstallAsync(
            workspace, home, ["hook", "snippet", "--session-start", "--client", "kimi,codex"]);

        exitCode.ShouldBe(1);
        error.ShouldContain("no client with a verified hook layout");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunInstallAsync(
        string workspace,
        string home,
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
        using var environment = new TestEnvironmentScope(home);
        AnsiConsole.Console = console;
        Environment.CurrentDirectory = workspace;
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        Console.SetOut(outputWriter);
        Console.SetError(errorWriter);
        try
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.Settings.Console = console;
                config.AddBranch("hook", hook =>
                {
                    hook.AddCommand<Squeezr.Cli.Commands.HookSessionStartCommand>("session-start");
                    hook.AddCommand<Squeezr.Cli.Commands.HookSnippetCommand>("snippet");
                });
                McpCommandRegistration.AddMcpBranch(config);
            });
            var exitCode = await app.RunAsync(args);
            return (exitCode, outputWriter.ToString(), errorWriter.ToString());
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

    private static JsonElement EntryOf(string output) =>
        JsonDocument.Parse(output.Trim()).RootElement.EnumerateArray().Single();

    // Names relative to the profile root, so two arms run in two different scratch homes stay comparable.
    private static string[] FilesIn(string root) =>
        Directory.Exists(root)
            ? [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .Order(StringComparer.Ordinal)]
            : [];
}
