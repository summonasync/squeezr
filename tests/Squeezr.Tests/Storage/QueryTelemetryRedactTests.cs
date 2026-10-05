using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Storage;

// queries.jsonl records the parameters a call was made with, literally — so a query that ran before the guard
// existed keeps quoting the secret path it asked about. The purge rewrites those values through the same
// classifier that refused the read, and the counters the file exists for have to survive the rewrite.
public class QueryTelemetryRedactTests
{
    private const string EnvPath = ".env";
    private const string TokensPath = "notes/tokens.txt";
    private const string AppPath = "src/App.cs";
    private const string ExamplePath = ".env.example";

    [Fact]
    public void Redact_ProtectedPathValue_IsReplacedAndItsLineKept()
    {
        var workspace = TelemetryStore();
        var session = Guid.NewGuid();
        Record(workspace, session, "expand", new { file = EnvPath, startLine = 4, contextLines = 2 });
        Record(workspace, session, "search", new { query = "Load", repo = "demo" });

        var redacted = QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly);

        redacted.ShouldBe(1);
        var entries = QueryTelemetry.ReadAll(workspace);
        entries.Count.ShouldBe(2);
        entries[0].Tool.ShouldBe("expand");
        entries[1].Tool.ShouldBe("search");
        var lines = File.ReadAllLines(SqueezrPaths.QueriesLogPath(workspace));
        lines[0].ShouldNotContain(EnvPath);
        lines[0].ShouldContain(QueryTelemetry.RedactedPlaceholder);
        lines[0].ShouldContain("\"startLine\":4");
        lines[1].ShouldContain("\"query\":\"Load\"");
        lines[1].ShouldContain("\"repo\":\"demo\"");
    }

    [Fact]
    public void Redact_UnrelatedValues_AreLeftByteForByte()
    {
        var workspace = TelemetryStore();
        var session = Guid.NewGuid();
        Record(workspace, session, "expand", new { file = AppPath, startLine = 9 });
        Record(workspace, session, "search", new { query = $"how is {ExamplePath} loaded", repo = "demo" });
        var before = File.ReadAllBytes(SqueezrPaths.QueriesLogPath(workspace));

        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(0);

        File.ReadAllBytes(SqueezrPaths.QueriesLogPath(workspace)).ShouldBe(before);
    }

    [Fact]
    public void Redact_NestedAndArrayValues_AreCovered()
    {
        var workspace = TelemetryStore();
        File.AppendAllText(
            SqueezrPaths.QueriesLogPath(workspace),
            """
            {"ts":"2026-10-05T10:00:00Z","sessionId":"11111111-1111-1111-1111-111111111111","tool":"batch","params":{"targets":[{"path":"deploy/keys/monkey.pem"},{"path":"src/App.cs"}],"note":{"file":".env"}},"tokensUsed":3,"truncated":false,"hits":1,"durationMs":4,"cacheHit":false,"indexAgeSeconds":null,"client":"cli"}

            """);

        var redacted = QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly);

        redacted.ShouldBe(2);
        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace));
        raw.ShouldNotContain("monkey.pem");
        raw.ShouldNotContain("\"file\":\".env\"");
        raw.ShouldContain(AppPath);
    }

    [Fact]
    public void Redact_HonoursTheConfiguredExtrasOfTheSameClassifier()
    {
        var workspace = TelemetryStore();
        var session = Guid.NewGuid();
        Record(workspace, session, "expand", new { file = TokensPath, startLine = 1 });

        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(0);

        var redacted = QueryTelemetry.RedactProtectedParams(
            workspace, path => SecretGuard.IsProtected(path, ["**/tokens.txt"]));

        redacted.ShouldBe(1);
        File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace)).ShouldNotContain(TokensPath);
    }

    [Fact]
    public void Redact_Twice_IsIdempotent()
    {
        var workspace = TelemetryStore();
        Record(workspace, Guid.NewGuid(), "expand", new { file = EnvPath, startLine = 1 });

        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(1);
        var afterFirst = File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace));
        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(0);

        File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace)).ShouldBe(afterFirst);
    }

    [Fact]
    public void Redact_UnparsableLine_IsSkippedAndTheRestIsStillScrubbed()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        File.AppendAllText(log, "}{ this line is not json\n");
        Record(workspace, Guid.NewGuid(), "expand", new { file = EnvPath, startLine = 2 });

        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(1);

        File.ReadAllLines(log)[0].ShouldBe("}{ this line is not json");
        File.ReadAllText(log).ShouldNotContain("\"file\":\".env\"");
    }

    [Fact]
    public void Redact_WithoutTelemetryFile_ReturnsZeroAndWritesNothing()
    {
        var workspace = TelemetryStore();

        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(0);

        File.Exists(SqueezrPaths.QueriesLogPath(workspace)).ShouldBeFalse();
    }

    private static bool BuiltInsOnly(string path) => SecretGuard.IsProtected(path, []);

    private static void Record(string workspace, Guid session, string tool, object parameters) =>
        QueryTelemetry.Record(workspace, session, tool, parameters, 120, false, 1, 5);

    private static string TelemetryStore()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(workspace));
        return workspace;
    }
}
