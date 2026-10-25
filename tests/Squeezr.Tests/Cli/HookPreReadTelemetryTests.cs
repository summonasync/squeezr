using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class HookPreReadTelemetryTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

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

    private static string Payload(string sessionId, string filePath) => JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        tool_name = "Read",
        tool_input = new { file_path = filePath }
    });

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
