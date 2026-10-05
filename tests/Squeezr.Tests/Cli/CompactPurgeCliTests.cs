using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Cli;

// What the operator actually runs and actually reads: `squeezr compact` is the forced purge pass over a store
// that was built before the guard. The counters have to be there, and the protected names must not be — the
// one line of this contract that an agent sees is the line that must not become an existence oracle.
[Collection("CliProcessGlobals")]
public sealed class CompactPurgeCliTests
{
    private const string PemPath = "deploy/keys/monkey.pem";
    private const string SecretsPath = "config/secrets.json";
    private const string AppPath = "src/App.cs";
    private const string PemCanary = "T4-CLI-CANARY-PEM";
    private const string SecretsCanary = "T4-CLI-CANARY-JSON";

    [Fact]
    public async Task Compact_Json_CountsThePurgeAndNamesNothing()
    {
        var root = await IndexedWorkspaceAsync();
        PlantPreGuardRows(root);
        var storeSizeBefore = new FileInfo(SqueezrPaths.MetaDbPath(root)).Length;
        var trigramSizeBefore = new FileInfo(SqueezrPaths.TrigramIndexPath(root)).Length;

        var compact = await CliTestProcess.RunAsync(root, "compact", "--json");

        compact.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(compact.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("pathsPurged").GetInt32().ShouldBe(2);
        payload.GetProperty("rowsPurged").GetInt32().ShouldBeGreaterThanOrEqualTo(4);
        payload.GetProperty("reapJournalRedacted").GetInt32().ShouldBe(0);
        payload.GetProperty("telemetryValuesRedacted").GetInt32().ShouldBe(1);
        payload.GetProperty("storeSizeBeforeBytes").GetInt64().ShouldBe(storeSizeBefore);
        payload.GetProperty("sizeBeforeBytes").GetInt64().ShouldBe(trigramSizeBefore);
        payload.GetProperty("storeSizeAfterBytes").GetInt64().ShouldBeLessThanOrEqualTo(storeSizeBefore);
        var output = compact.StdOut + compact.StdErr;
        output.ShouldNotContain(PemPath);
        output.ShouldNotContain(SecretsPath);
        output.ShouldNotContain(PemCanary);
        output.ShouldNotContain("monkey.pem");
        output.ShouldContain("secret guard: 2 stored path(s) purged");
        AssertStoreBytesAreClean(root);
    }

    [Fact]
    public async Task Compact_Plain_PrintsPurgeCountersAndNeverAName()
    {
        var root = await IndexedWorkspaceAsync();
        PlantPreGuardRows(root);

        var compact = await CliTestProcess.RunAsync(root, "compact");

        compact.ExitCode.ShouldBe(0);
        compact.StdErr.ShouldContain("secret guard: 2 stored path(s) purged from the index store");
        compact.StdErr.ShouldContain("recorded parameter value(s) redacted");
        var output = compact.StdOut + compact.StdErr;
        output.ShouldNotContain(PemPath);
        output.ShouldNotContain(SecretsPath);
        output.ShouldNotContain(PemCanary);
        output.ShouldNotContain("monkey");
        compact.StdOut.ShouldContain("meta.db");
        compact.StdOut.ShouldContain("trigram.idx");
    }

    [Fact]
    public async Task Compact_OnACleanStore_ReportsNoPurgeAndKeepsTheSearchAnswering()
    {
        var root = await IndexedWorkspaceAsync(residualTelemetry: false);

        var compact = await CliTestProcess.RunAsync(root, "compact", "--json");

        compact.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(compact.StdOut.Trim());
        doc.RootElement.GetProperty("pathsPurged").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("rowsPurged").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("telemetryValuesRedacted").GetInt32().ShouldBe(0);
        doc.RootElement.TryGetProperty("warnings", out _).ShouldBeFalse();

        var search = await CliTestProcess.RunAsync(root, "search", "Greeting", "--json");
        search.ExitCode.ShouldBe(0);
        search.StdOut.ShouldContain(AppPath);
    }

    [Fact]
    public async Task Compact_AfterThePurge_IndexDoesNotBringTheRowsBack()
    {
        var root = await IndexedWorkspaceAsync();
        PlantPreGuardRows(root);
        (await CliTestProcess.RunAsync(root, "compact", "--json")).ExitCode.ShouldBe(0);

        var index = await CliTestProcess.RunAsync(root, "index", "--json");

        index.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(index.StdOut.Trim());
        doc.RootElement.GetProperty("repos")[0].GetProperty("deleted").GetInt32().ShouldBe(0);
        StoredPaths(root).ShouldBe([AppPath], StringComparer.Ordinal);
        AssertStoreBytesAreClean(root);
    }

    private static List<string> StoredPaths(string root)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        return [.. store.ListAllFiles().Select(f => f.Path)];
    }

    // The store is built by the real CLI; the secret files are on disk, so ingestion refuses them and only the
    // planted rows (or the residual telemetry, when the scenario is about that) give the purge something to do.
    private static async Task<string> IndexedWorkspaceAsync(bool residualTelemetry = true)
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Write("src/App.cs", "public class App { public string Greeting => \"T4-CLI-PUBLIC\"; }\n");
        Write(PemPath, $"{PemCanary}\n");
        Write(SecretsPath, $"{{ \"token\": \"{SecretsCanary}\" }}\n");
        (await CliTestProcess.RunAsync(root, "add", "code", "--name", "demo", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        if (residualTelemetry)
        {
            // a query that ran before the guard existed: it recorded the path it asked about, literally
            QueryTelemetry.Record(
                root, Guid.NewGuid(), "expand", new { file = PemPath, startLine = 1 }, 80, false, 1, 3);
        }

        return root;

        void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(root, "code", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }

    // rows written the way a pre-guard index wrote them — the ingestion guard is in force, so the store has to
    // be built by hand to have anything for the purge to find
    private static void PlantPreGuardRows(string root) =>
        SecretGuardFixture.PlantPreGuardRows(root, [PemPath, SecretsPath]);

    private static void AssertStoreBytesAreClean(string root)
    {
        var meta = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(SqueezrPaths.MetaDbPath(root)));
        var trigram = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(SqueezrPaths.TrigramIndexPath(root)));
        foreach (var canary in new[] { PemCanary, SecretsCanary })
        {
            meta.ShouldNotContain(canary);
            trigram.ShouldNotContain(canary);
        }

        foreach (var path in new[] { PemPath, SecretsPath })
        {
            meta.ShouldNotContain(path);
            trigram.ShouldNotContain(path);
        }

        Directory.EnumerateFiles(SqueezrPaths.StoreDir(root), "meta*")
            .Where(f => Path.GetExtension(f).Equals(".db", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ShouldBe(["meta.db"], StringComparer.Ordinal);
        File.ReadAllLines(SqueezrPaths.QueriesLogPath(root)).ShouldNotContain(
            line => line.Contains(PemPath, StringComparison.Ordinal));
    }
}
