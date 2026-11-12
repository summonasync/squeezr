using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
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
    public async Task LargeProtectedFile_AnswersLikeAMissingOne()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var secret = Path.Combine(root, ".env");
        await File.WriteAllTextAsync(secret, new string('x', 300 * 1024));

        var protectedRun = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(secret), root, "hook", "pre-read");
        var missingRun = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(Path.Combine(root, "does-not-exist.cs")), root, "hook", "pre-read");

        protectedRun.ExitCode.ShouldBe(0);
        protectedRun.StdOut.ShouldBeEmpty();
        missingRun.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task LargeFileProtectedByAnchoredExtension_AnswersLikeAMissingOne()
    {
        // the workspace is built in-process here so the config edit lands in the store the child reads
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        Directory.CreateDirectory(Path.Combine(root, "repo", "infra"));
        var secret = Path.Combine(root, "repo", "infra", "VaultKeys.cs");
        await File.WriteAllTextAsync(secret, new string('x', 300 * 1024));
        await File.WriteAllTextAsync(Path.Combine(root, "generated.cs"), new string('x', 300 * 1024));
        manager.AddRepo("repo", "demo");
        SecretGuardFixture.SetGuardExtra(root, "infra/VaultKeys.cs");

        var controlRun = await CliTestProcess.RunPipedAsync(
            SqueezrHome.Resolve(), ReadPayload(Path.Combine(root, "generated.cs")), root, "hook", "pre-read");
        var protectedRun = await CliTestProcess.RunPipedAsync(
            SqueezrHome.Resolve(), ReadPayload(secret), root, "hook", "pre-read");
        var missingRun = await CliTestProcess.RunPipedAsync(
            SqueezrHome.Resolve(), ReadPayload(Path.Combine(root, "does-not-exist.cs")), root, "hook", "pre-read");

        controlRun.StdOut.ShouldContain("expand_span");
        protectedRun.ExitCode.ShouldBe(0);
        protectedRun.StdOut.ShouldBeEmpty();
        missingRun.StdOut.ShouldBeEmpty();
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

    [Theory]
    [InlineData("Read", true)]
    [InlineData("read_file", true)]
    [InlineData("ReadFile", true)]
    [InlineData("Grep", false)]
    [InlineData("glob", false)]
    [InlineData("write_file", false)]
    [InlineData("read", false)]
    [InlineData("READ_FILE", false)]
    [InlineData("Read_File", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReadTool_DeclaredOrOffName_AnswersAsDeclared(string? toolName, bool expected) =>
        HookPreReadToolNames.IsReadTool(toolName).ShouldBe(expected);

    [Theory]
    [InlineData("Read")]
    [InlineData("read_file")]
    [InlineData("ReadFile")]
    public async Task WideRange_AcceptedToolName_EmitsAdvisoryNudge(string toolName)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(smallFile, limit: 2000, toolName: toolName), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        hook.GetProperty("hookEventName").GetString().ShouldBe("PreToolUse");
        var context = hook.GetProperty("additionalContext").GetString();
        context.ShouldNotBeNullOrWhiteSpace();
        context!.ShouldContain("expand_span");
        context.ShouldContain("code_search");
        doc.RootElement.TryGetProperty("permissionDecision", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("glob")]
    [InlineData("write_file")]
    [InlineData("read")]
    public async Task WideRange_OffToolName_PrintsNothing(string toolName)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(smallFile, limit: 2000, toolName: toolName), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("read_file")]
    [InlineData("ReadFile")]
    public async Task LargeFileProtectedByAnchoredExtension_AcceptedToolName_AnswersLikeAMissingOne(string toolName)
    {
        // same in-process fixture as the name-only pin above: the control run proves the accepted
        // name does nudge an unguarded big file, so the silence below belongs to the guard, not to the tool
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        Directory.CreateDirectory(Path.Combine(root, "repo", "infra"));
        var secret = Path.Combine(root, "repo", "infra", "VaultKeys.cs");
        await File.WriteAllTextAsync(secret, new string('x', 300 * 1024));
        await File.WriteAllTextAsync(Path.Combine(root, "generated.cs"), new string('x', 300 * 1024));
        manager.AddRepo("repo", "demo");
        SecretGuardFixture.SetGuardExtra(root, "infra/VaultKeys.cs");

        var controlRun = await CliTestProcess.RunPipedAsync(
            SqueezrHome.Resolve(), ReadPayload(Path.Combine(root, "generated.cs"), toolName: toolName), root, "hook", "pre-read");
        var protectedRun = await CliTestProcess.RunPipedAsync(
            SqueezrHome.Resolve(), ReadPayload(secret, toolName: toolName), root, "hook", "pre-read");

        controlRun.StdOut.ShouldContain("expand_span");
        protectedRun.ExitCode.ShouldBe(0);
        protectedRun.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task QuotedRangeArgs_OnLargeFile_EmitTheByteSameNudgeAsNumericArgs()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var numericRun = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(bigFile, limit: 437, offset: 400, toolName: "read_file"), root, "hook", "pre-read");
        var quotedRun = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(bigFile, "437", "400"), root, "hook", "pre-read");

        numericRun.ExitCode.ShouldBe(0);
        quotedRun.ExitCode.ShouldBe(0);
        quotedRun.StdOut.ShouldContain("expand_span");
        quotedRun.StdOut.ShouldBe(numericRun.StdOut);
    }

    [Theory]
    [InlineData("399", false)]
    [InlineData("400", true)]
    [InlineData("401", true)]
    [InlineData("2000", true)]
    public async Task QuotedLimit_Boundary_AnswersAsTheNumericArm(string limit, bool nudges)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var smallFile = Path.Combine(root, "small.cs");
        await File.WriteAllTextAsync(smallFile, "class Small { }");

        var result = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(smallFile, limit), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        if (nudges)
        {
            result.StdOut.ShouldContain("expand_span");
        }
        else
        {
            result.StdOut.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task QuotedOffsetWithNumericLimit_OnLargeFile_EmitsAdvisoryNudge()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(
            home, MixedRangePayload(bigFile, "437", 400), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("expand_span");
    }

    [Fact]
    public async Task QuotedOffsetWithoutLimit_LargeFile_EmitsAdvisoryNudge()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(bigFile, null, "5000"), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("expand_span");
    }

    [Fact]
    public async Task QuotedNarrowLimit_OnLargeFile_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var result = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(bigFile, "399", "437"), root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task TruncatedPayload_OnLargeFile_PrintsNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var complete = ReadPayload(bigFile, toolName: "read_file");
        var result = await CliTestProcess.RunPipedAsync(
            home, complete[..(complete.Length - 2)], root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("[{\"tool_name\":\"read_file\"}]")]
    [InlineData("\"read_file\"")]
    [InlineData("42")]
    public async Task WrongRootTypePayload_PrintsNothing(string payload)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);

        var result = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"limit\":\"wide\"}")]
    [InlineData("{\"limit\":\"400abc\"}")]
    [InlineData("{\"limit\":\"\"}")]
    [InlineData("{\"offset\":\"wide\",\"limit\":400}")]
    [InlineData("{\"offset\":\"400abc\",\"limit\":400}")]
    public async Task QuotedNonNumericRangeValue_OnLargeFile_PrintsNothing(string rangeFragment)
    {
        var home = TestDirs.NewTempDir();
        var root = await NewInitializedWorkspaceAsync(home);
        var bigFile = Path.Combine(root, "generated.cs");
        await File.WriteAllTextAsync(bigFile, new string('x', 300 * 1024));

        var payload = "{\"tool_name\":\"read_file\",\"tool_input\":{\"file_path\":\"" + bigFile + "\"" +
                      rangeFragment.TrimStart('{').TrimEnd('}') + "}}";

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

    private static string ReadPayload(string filePath, int? limit = null, int? offset = null, string toolName = "Read")
    {
        var range = limit is null ? string.Empty : $",\"limit\":{limit}";
        var start = offset is null ? string.Empty : $",\"offset\":{offset}";
        return "{\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" + filePath + "\"" + range + start + "}}";
    }

    // the shape Qwen 0.27.1 forwards for a paged read: the same fields, the numbers quoted
    private static string QuotedRangePayload(string filePath, string? limit = null, string? offset = null, string toolName = "read_file")
    {
        var range = limit is null ? string.Empty : $",\"limit\":\"{limit}\"";
        var start = offset is null ? string.Empty : $",\"offset\":\"{offset}\"";
        return "{\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" + filePath + "\"" + range + start + "}}";
    }

    private static string MixedRangePayload(string filePath, string offset, int limit, string toolName = "read_file") =>
        "{\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" + filePath +
        "\",\"limit\":" + limit + ",\"offset\":\"" + offset + "\"}}";
}
