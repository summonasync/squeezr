using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class HookPreReadTelemetryTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private const string NumericSessionId = "11111111-2222-3333-4444-666666666666";
    private const string QuotedSessionId = "99999999-8888-7777-6666-555555555555";

    [Fact]
    public async Task PreReadHook_AppendsTelemetryWithSessionAndPath()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");

        var run = await CliTestProcess.RunPipedAsync(
            home, Payload(SessionId, Path.Combine(root, "src", "Foo.cs")), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBe(string.Empty);
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        entry.Client.ShouldBe("hook");
        entry.SessionId.ShouldBe(new Guid(SessionId));
        entry.Hits.ShouldBe(0);
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("tool").GetString().ShouldBe("Read");
        parameters.GetProperty("file").GetString().ShouldBe(Path.Combine(root, "src", "Foo.cs"));
    }

    [Fact]
    public async Task PreReadHook_RedactsProtectedPathBeforeWriting()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, ".env", "SECRET=1");

        var run = await CliTestProcess.RunPipedAsync(
            home, Payload(SessionId, Path.Combine(root, ".env")), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("file").GetString().ShouldBe(QueryTelemetry.RedactedPlaceholder);
    }

    [Fact]
    public async Task PreReadHook_LegacyPayloadWithoutSessionId_LandsUnattributed()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");

        var payload = JsonSerializer.Serialize(new
        {
            tool_name = "Read",
            tool_input = new { file_path = Path.Combine(root, "src", "Foo.cs") }
        });

        var run = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        entry.SessionId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task PreReadHook_GarbagePayload_StillExitsClean()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);

        var run = await CliTestProcess.RunPipedAsync(home, "not json at all", root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        QueryTelemetry.ReadAll(root).ShouldNotContain(e => e.Tool == "pre-read");
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("read_file")]
    [InlineData("ReadFile")]
    public async Task PreReadHook_AcceptedToolName_AppendsRowWithVerbatimTool(string toolName)
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");

        var run = await CliTestProcess.RunPipedAsync(
            home, Payload(SessionId, Path.Combine(root, "src", "Foo.cs"), toolName), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        entry.Client.ShouldBe("hook");
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("tool").GetString().ShouldBe(toolName);
        parameters.GetProperty("file").GetString().ShouldBe(Path.Combine(root, "src", "Foo.cs"));
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("glob")]
    [InlineData("write_file")]
    [InlineData("read")]
    [InlineData("READ_FILE")]
    public async Task PreReadHook_OffToolName_AppendsNoRowAndPrintsNothing(string toolName)
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");

        var run = await CliTestProcess.RunPipedAsync(
            home, Payload(SessionId, Path.Combine(root, "src", "Foo.cs"), toolName), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        QueryTelemetry.ReadAll(root).ShouldNotContain(e => e.Tool == "pre-read");
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("read_file")]
    [InlineData("ReadFile")]
    public async Task PreReadHook_GuardedPath_AcceptedToolName_AppendsOnlyTheRedactedRow(string toolName)
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, ".env", new string('x', 300 * 1024));

        var run = await CliTestProcess.RunPipedAsync(
            home, Payload(SessionId, Path.Combine(root, ".env"), toolName), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("tool").GetString().ShouldBe(toolName);
        parameters.GetProperty("file").GetString().ShouldBe(QueryTelemetry.RedactedPlaceholder);
    }

    [Fact]
    public async Task PreReadHook_QuotedRangeArgs_AppendsTheSameRowAsTheNumericArm()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");
        var filePath = Path.Combine(root, "src", "Foo.cs");

        var numericRun = await CliTestProcess.RunPipedAsync(
            home, RangePayload(NumericSessionId, filePath, 400, 437), root, "hook", "pre-read");
        var quotedRun = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(QuotedSessionId, filePath, "400", "437"), root, "hook", "pre-read");

        numericRun.ExitCode.ShouldBe(0);
        quotedRun.ExitCode.ShouldBe(0);
        var rows = QueryTelemetry.ReadAll(root).Where(e => e.Tool == "pre-read").ToArray();
        var numericRow = rows.Single(e => e.SessionId == new Guid(NumericSessionId));
        var quotedRow = rows.Single(e => e.SessionId == new Guid(QuotedSessionId));

        quotedRow.Client.ShouldBe(numericRow.Client);
        quotedRow.ClientVersion.ShouldBe(numericRow.ClientVersion);
        quotedRow.Hits.ShouldBe(numericRow.Hits);
        quotedRow.TokensUsed.ShouldBe(numericRow.TokensUsed);
        quotedRow.DurationMs.ShouldBe(numericRow.DurationMs);
        quotedRow.Truncated.ShouldBe(numericRow.Truncated);
        quotedRow.Mapped.ShouldBe(numericRow.Mapped);
        var quotedParams = (JsonElement)quotedRow.Params!;
        var numericParams = (JsonElement)numericRow.Params!;
        quotedParams.GetProperty("tool").GetString().ShouldBe("read_file");
        quotedParams.GetProperty("file").GetString().ShouldBe(filePath);
        quotedParams.GetRawText().ShouldBe(numericParams.GetRawText());
    }

    [Fact]
    public async Task PreReadHook_QuotedNarrowLimit_RecordsTheReadWithoutAdvising()
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);
        WriteFile(root, "src/Foo.cs", "public class Foo { }");
        var filePath = Path.Combine(root, "src", "Foo.cs");

        var run = await CliTestProcess.RunPipedAsync(
            home, QuotedRangePayload(QuotedSessionId, filePath, "399", "437"), root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        var entry = QueryTelemetry.ReadAll(root).Single(e => e.Tool == "pre-read");
        entry.SessionId.ShouldBe(new Guid(QuotedSessionId));
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("tool").GetString().ShouldBe("read_file");
    }

    [Theory]
    [InlineData("{\"session_id\":\"99999999-8888-7777-6666-555555555555\",\"tool_name\":\"read_file\",\"tool_input\":{\"limit\":")]
    [InlineData("[{\"tool_name\":\"read_file\"}]")]
    [InlineData("\"read_file\"")]
    [InlineData("{\"tool_name\":\"read_file\",\"tool_input\":{\"file_path\":\"/etc/hostname\",\"limit\":\"wide\"}}")]
    public async Task PreReadHook_BrokenPayload_AppendsNoRowAndExitsZero(string payload)
    {
        var home = SqueezrHome.Resolve();
        var root = await NewRegisteredWorkspaceAsync(home);

        var run = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "pre-read");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        QueryTelemetry.ReadAll(root).ShouldNotContain(e => e.Tool == "pre-read");
    }

    private static string Payload(string sessionId, string filePath, string toolName = "Read") =>
        JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            tool_name = toolName,
            tool_input = new { file_path = filePath }
        });

    private static string RangePayload(string sessionId, string filePath, int limit, int offset, string toolName = "read_file") =>
        "{\"session_id\":\"" + sessionId + "\",\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" +
        filePath + "\",\"limit\":" + limit + ",\"offset\":" + offset + "}}";

    // the shape Qwen 0.27.1 forwards for a paged read: the same fields, the numbers quoted
    private static string QuotedRangePayload(string sessionId, string filePath, string limit, string offset, string toolName = "read_file") =>
        "{\"session_id\":\"" + sessionId + "\",\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" +
        filePath + "\",\"limit\":\"" + limit + "\",\"offset\":\"" + offset + "\"}}";

    private static async Task<string> NewRegisteredWorkspaceAsync(string home)
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(home, "", root, "init", "--json")).ExitCode.ShouldBe(0);
        return root;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
