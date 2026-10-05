using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

// The same counters through the real process: what an agent reading `index --json` / `--events` and a human
// reading `squeezr status` actually see when a pass refused secrets.
[Collection("CliProcessGlobals")]
public sealed class IndexGuardCliTests
{
    private const string PemPath = "deploy/keys/monkey.pem";
    private const string EnvPath = ".env";
    private const string EnvCanary = "T2-CANARY-ENV-VALUE";

    [Fact]
    public async Task Index_Json_CarriesTheWithheldCounterAndNoPath()
    {
        var root = await NewRepoWithSecretsAsync();

        var index = await CliTestProcess.RunAsync(root, "index", "--json");

        index.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(index.StdOut.Trim());
        var repo = doc.RootElement.GetProperty("repos")[0];
        repo.GetProperty("guardWithheld").GetInt32().ShouldBe(2);
        repo.GetProperty("guardWithheldBuiltIns").GetInt32().ShouldBe(2);
        repo.GetProperty("guardDropped").GetInt32().ShouldBe(0);
        repo.GetProperty("skipped").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        repo.GetProperty("files").GetInt32().ShouldBe(1);
        index.StdOut.ShouldNotContain(PemPath);
        index.StdOut.ShouldNotContain(EnvPath);
    }

    [Fact]
    public async Task Index_Events_EmitsNoFileIndexedLineForAWithheldPath()
    {
        var root = await NewRepoWithSecretsAsync();

        var index = await CliTestProcess.RunAsync(root, "index", "--events");

        index.ExitCode.ShouldBe(0);
        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain(l => l.Contains("\"fileIndexed\"", StringComparison.Ordinal));
        string.Join("|", lines).ShouldNotContain(PemPath);
        string.Join("|", lines).ShouldNotContain(EnvCanary);
        using var doc = JsonDocument.Parse(lines.Last());
        doc.RootElement.GetProperty("type").GetString().ShouldBe("completed");
        doc.RootElement.GetProperty("guardWithheld").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Index_PlainRun_PrintsTheWithheldLineAndNeverTheName()
    {
        var root = await NewRepoWithSecretsAsync();

        var index = await CliTestProcess.RunAsync(root, "index", "--no-semantic");

        index.ExitCode.ShouldBe(0);
        index.StdErr.ShouldContain("secret guard: 2 file(s) withheld from indexing (built-in patterns)");
        index.StdErr.ShouldNotContain(PemPath);
    }

    [Fact]
    public async Task Status_Json_LedgersTheIngestionSideWithoutNamingAPath()
    {
        var root = await NewRepoWithSecretsAsync();
        await CliTestProcess.RunAsync(root, "index", "--json");

        var status = await CliTestProcess.RunAsync(root, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var guard = doc.RootElement.GetProperty("repos")[0].GetProperty("guard");
        guard.GetProperty("withheld").GetInt32().ShouldBe(2);
        guard.GetProperty("withheldByBuiltIns").GetInt32().ShouldBe(2);
        guard.GetProperty("dropped").GetInt32().ShouldBe(0);
        var output = status.StdOut + status.StdErr;
        output.ShouldNotContain(PemPath);
        output.ShouldNotContain(EnvPath);
        output.ShouldContain("secret guard: 2 file(s) withheld from indexing");
    }

    [Fact]
    public async Task Status_PlainRun_ShowsTheLedgerColumns()
    {
        var root = await NewRepoWithSecretsAsync();
        await CliTestProcess.RunAsync(root, "index", "--json");

        var status = await CliTestProcess.RunAsync(root, "status");

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("Withheld");
        status.StdOut.ShouldContain("secret guard: 2 file(s) withheld from indexing");
        status.StdOut.ShouldNotContain(PemPath);
    }

    private static async Task<string> NewRepoWithSecretsAsync()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Write("src/App.cs", "public class App { public void Run() { } }\n");
        Write(EnvPath, $"OPENAI_API_KEY={EnvCanary}\n");
        Write(PemPath, "T2-CANARY-PEM-VALUE\n");
        (await CliTestProcess.RunAsync(root, "add", "code", "--name", "demo", "--json"))
            .ExitCode.ShouldBe(0);
        return root;

        void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(root, "code", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }
}
