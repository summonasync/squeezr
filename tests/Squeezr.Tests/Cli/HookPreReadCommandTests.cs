using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class HookPreReadCommandTests
{
    [Fact]
    public async Task LargeFile_InWorkspace_EmitsAdvisoryNudge()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(bigFile), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        hook.GetProperty("hookEventName").GetString().ShouldBe("PreToolUse");
        var context = hook.GetProperty("additionalContext").GetString();
        context.ShouldNotBeNullOrWhiteSpace();
        context!.ShouldContain("expand_span");
        context.ShouldContain("symbol_lookup");
        doc.RootElement.TryGetProperty("permissionDecision", out _).ShouldBeFalse();
        hook.TryGetProperty("permissionDecision", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task LargeFile_OutsideAnyWorkspace_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        await NewInitializedWorkspaceAsync(home);
        var outside = TestDirs.NewTempDir();
        var bigFile = Path.Combine(outside, "unrelated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(bigFile), outside, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task SmallFile_InWorkspace_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(smallFile), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task NarrowRange_OnLargeFile_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(bigFile, limit: 50), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task WideRange_OnSmallFile_EmitsAdvisoryNudge()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(smallFile, limit: 2000), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task MissingFile_InWorkspace_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);

        var result = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(Path.Combine(root, "does-not-exist.cs")), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task OffsetWithoutLimit_LargeFile_EmitsAdvisoryNudge()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(bigFile, offset: 5000), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("expand_span");
    }

    [Fact]
    public async Task OffsetWithoutLimit_SmallFile_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(home, ReadPayload(smallFile, offset: 5000), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task OversizedStdin_Garbage_FailsOpen()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);

        var result = await CliTestProcess.RunPipedAsync(
            home, new string('x', 200 * 1024), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task OversizedStdin_NudgeWorthyPayloadBeyondCap_FailsOpen()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));
        var oversized = ReadPayload(bigFile) + new string(' ', 70 * 1024);

        var result = await CliTestProcess.RunPipedAsync(home, oversized, root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"tool_name\":\"Read\"}")]
    [InlineData("{\"tool_name\":\"Grep\",\"tool_input\":{\"file_path\":\"/etc/hostname\"}}")]
    public async Task MalformedOrOffToolPayload_PrintsNothingAndExitsZero(string payload)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);

        var result = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    private static async Task<string> NewInitializedWorkspaceAsync(string home)
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
        return root;
    }

    private static string ReadPayload(string filePath, int? limit = null, int? offset = null)
    {
        var range = limit is null ? string.Empty : $",\"limit\":{limit}";
        var start = offset is null ? string.Empty : $",\"offset\":{offset}";
        return "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"" + filePath + "\"" + range + start + "}}";
    }
}
