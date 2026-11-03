using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Cli;

// The machine-readable half of the ingestion guard: a pass that refused secrets has to say so in
// --json/--events and in both ledgers, and no line of that output may carry the path it refused.
public class IndexGuardOutputTests
{
    private const string PemPath = "deploy/keys/monkey.pem";
    private const string EnvPath = ".env";

    [Fact]
    public void Completed_CarriesGuardCountersOnTheLineAndPerRepo()
    {
        var writer = new StringWriter();
        var result = new IndexResult(
            [new RepoIndexResult("demo", 1, 0, 2, 3, 5, 0, null, 3, 2, 2)],
            DateTime.UtcNow, [],
            GuardWithheld: 3,
            GuardWithheldBuiltIns: 2,
            GuardDropped: 2);

        new IndexEventWriter(writer).Completed(result, 42, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        var root = doc.RootElement;
        root.GetProperty("guardWithheld").GetInt32().ShouldBe(3);
        root.GetProperty("guardDropped").GetInt32().ShouldBe(2);
        var repo = root.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.GetProperty("guardWithheld").GetInt32().ShouldBe(3);
        repo.GetProperty("guardWithheldBuiltIns").GetInt32().ShouldBe(2);
        repo.GetProperty("guardDropped").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Completed_WithoutARefusal_StillDeclaresTheCounterAsZero()
    {
        var writer = new StringWriter();
        var result = new IndexResult([new RepoIndexResult("demo", 1, 0, 0, 0, 5, 0)], DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 42, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("guardWithheld").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("guardDropped").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Events_WithheldPath_EmitsNoFileIndexedLineAndOneCompletedCount()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        var writer = new StringWriter();
        var events = new IndexEventWriter(writer);

        var result = new WorkspaceIndexer().Index(
            workspace, progress: new IndexEventsRenderer(events));

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain(l => l.Contains("\"fileIndexed\"", StringComparison.Ordinal));
        string.Join("|", lines).ShouldNotContain(PemPath);
        string.Join("|", lines).ShouldNotContain($"\"{EnvPath}\"");
        result.GuardWithheld.ShouldBe(2);
    }

    [Fact]
    public void StatusReport_CarriesTheIngestionTallyOfTheLastPass()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);
        var status = new WorkspaceManager(workspace).GetStatus();

        var report = WorkspaceStatusReport.Create(status, StaleFileDetector.Detect(workspace), "ollama");

        var repo = report.Repos.ShouldHaveSingleItem();
        repo.Guard.ShouldNotBeNull().Withheld.ShouldBe(2);
        repo.Guard.WithheldByBuiltIns.ShouldBe(2);
        repo.Guard.WithheldByConfig.ShouldBe(0);
        repo.Guard.Dropped.ShouldBe(0);
        report.Warnings.ShouldContain(
            "secret guard: 2 file(s) withheld from indexing (built-in patterns), 0 protected row(s) " +
            "removed from the store — paths are not named");
        JsonSerializer(report).ShouldNotContain(PemPath);
        JsonSerializer(report).ShouldNotContain(EnvPath);
    }

    [Fact]
    public void StatusReport_SplitsTheOriginsWhenBothHalvesDecided()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        IngestionWorkspaces.WriteFile(workspace, "notes/tokens.txt", "T2-CANARY-TOKENS-VALUE\n");
        new WorkspaceIndexer().Index(workspace);

        var report = WorkspaceStatusReport.Create(
            new WorkspaceManager(workspace).GetStatus(), [], "ollama");

        report.Repos.ShouldHaveSingleItem().Guard.ShouldNotBeNull().WithheldByConfig.ShouldBe(1);
        report.Warnings.ShouldContain(w =>
            w.Contains("3 file(s) withheld from indexing (2 built-in patterns, 1 indexing.secretGuardExtra)",
                StringComparison.Ordinal));
    }

    [Fact]
    public void StatusReport_WithoutARefusalCarriesNoGuardBlockAndNoWarning()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();

        var report = WorkspaceStatusReport.Create(
            new WorkspaceManager(workspace).GetStatus(), StaleFileDetector.Detect(workspace), "ollama");

        report.Repos.ShouldHaveSingleItem().Guard.ShouldBeNull();
        report.Warnings.ShouldNotContain(w => w.Contains("secret guard", StringComparison.Ordinal));
    }

    private static string JsonSerializer(WorkspaceStatusReport report) =>
        System.Text.Json.JsonSerializer.Serialize(report, Squeezr.Core.Serialization.SqueezrJson.OutputOptions);
}
