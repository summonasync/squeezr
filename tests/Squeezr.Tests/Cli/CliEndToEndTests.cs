using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

public sealed class CliEndToEndTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Init_Add_Status_Drop_RoundTrip_WithJsonOutput()
    {
        var root = TestDirs.NewTempDir();

        var init = await RunCliAsync(root, "init", "--name", "demo", "--json");
        init.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(init.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("workspaceName").GetString().ShouldBe("demo");
            doc.RootElement.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(root));
            doc.RootElement.GetProperty("createdFiles").GetArrayLength().ShouldBe(5);
        }

        File.Exists(Path.Combine(root, "squeezr.workspace.json")).ShouldBeTrue();
        File.Exists(SqueezrPaths.ConfigPath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.MetaDbPath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.StatePath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.ComposePath(root)).ShouldBeTrue();

        Directory.CreateDirectory(Path.Combine(root, "services", "billing"));
        var add = await RunCliAsync(root, "add", "services/billing", "--name", "billing", "--json");
        add.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(add.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("name").GetString().ShouldBe("billing");
            doc.RootElement.GetProperty("path").GetString().ShouldBe("services/billing");
        }

        var status = await RunCliAsync(root, "status", "--json");
        status.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(status.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("name").GetString().ShouldBe("demo");
            var repos = doc.RootElement.GetProperty("repos");
            repos.GetArrayLength().ShouldBe(1);
            repos[0].GetProperty("name").GetString().ShouldBe("billing");
            repos[0].GetProperty("files").GetInt32().ShouldBe(0);
            repos[0].GetProperty("symbols").GetInt32().ShouldBe(0);
            repos[0].GetProperty("chunks").GetInt32().ShouldBe(0);
        }

        var drop = await RunCliAsync(root, "drop", "--json");
        drop.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(drop.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("removed").GetArrayLength().ShouldBe(3);
            doc.RootElement.GetProperty("removed")[0].GetString().ShouldBe(".squeezr");
            doc.RootElement.GetProperty("hint").GetString().ShouldBe("docker compose down -v");
        }

        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeFalse();
        File.Exists(SqueezrPaths.ManifestPath(root)).ShouldBeFalse();
    }

    [Fact]
    public async Task Commands_WithoutWorkspace_ReturnErrorCode()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "status", "--json")).ExitCode.ShouldBe(1);
        (await RunCliAsync(root, "add", ".", "--json")).ExitCode.ShouldBe(1);
        (await RunCliAsync(root, "drop", "--json")).ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Init_Twice_ReturnsErrorCode()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        var second = await RunCliAsync(root, "init", "--json");
        second.ExitCode.ShouldBe(1);
        second.StdErr.ShouldContain("already initialized");
    }

    [Fact]
    public async Task Index_Then_Status_ReportRealStats()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "a.cs"), "class Alpha { void Run() { } }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index", "--json");
        index.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(index.StdOut.Trim()))
        {
            var repos = doc.RootElement.GetProperty("repos");
            repos.GetArrayLength().ShouldBe(1);
            repos[0].GetProperty("name").GetString().ShouldBe("code");
            repos[0].GetProperty("added").GetInt32().ShouldBe(1);
            repos[0].GetProperty("modified").GetInt32().ShouldBe(0);
            repos[0].GetProperty("deleted").GetInt32().ShouldBe(0);
            repos[0].GetProperty("files").GetInt32().ShouldBe(1);
        }

        File.Exists(Path.Combine(root, ".squeezr", "trigram.idx")).ShouldBeTrue();

        var status = await RunCliAsync(root, "status", "--json");
        status.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(status.StdOut.Trim()))
        {
            var repos = doc.RootElement.GetProperty("repos");
            repos[0].GetProperty("files").GetInt32().ShouldBe(1);
            repos[0].GetProperty("lastIndexedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        }

        var second = await RunCliAsync(root, "index", "--json");
        second.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(second.StdOut.Trim()))
        {
            var repo = doc.RootElement.GetProperty("repos")[0];
            repo.GetProperty("added").GetInt32().ShouldBe(0);
            repo.GetProperty("modified").GetInt32().ShouldBe(0);
            repo.GetProperty("deleted").GetInt32().ShouldBe(0);
        }
    }

    [Fact]
    public async Task Search_FindsIndexedContent_WithJsonOutput()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Alpha.cs"), "class Alpha { }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        var search = await RunCliAsync(root, "search", "Alpha", "--json", "--max-tokens", "1000");
        search.ExitCode.ShouldBe(0);

        var lines = search.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            var match = doc.RootElement;
            match.GetProperty("type").GetString().ShouldBe("match");
            match.GetProperty("path").GetString().ShouldBe("Alpha.cs");
            match.GetProperty("repo").GetString().ShouldBe("code");
            match.GetProperty("line").GetInt32().ShouldBe(1);
        }

        using (var doc = JsonDocument.Parse(lines[1]))
        {
            var summary = doc.RootElement;
            summary.GetProperty("type").GetString().ShouldBe("summary");
            summary.GetProperty("matches").GetInt32().ShouldBe(1);
            summary.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
            summary.GetProperty("maxTokens").GetInt32().ShouldBe(1000);
            summary.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        }

        var bare = TestDirs.NewTempDir();
        var missing = await RunCliAsync(bare, "search", "Alpha", "--json");
        missing.ExitCode.ShouldBe(1);
        missing.StdOut.ShouldBeEmpty();
        var missingError = ParseStdErrError(missing.StdErr);
        missingError.GetProperty("code").GetString().ShouldBe("index_not_built");
        missingError.GetProperty("message").GetString()!.ShouldContain("squeezr index");

        var invalid = await RunCliAsync(root, "search", "[", "--regex", "--json");
        invalid.ExitCode.ShouldBe(1);
        invalid.StdOut.ShouldBeEmpty();
        var invalidError = ParseStdErrError(invalid.StdErr);
        invalidError.GetProperty("code").GetString().ShouldBe("invalid_query");
        invalidError.GetProperty("message").GetString()!.ShouldContain("Invalid regex pattern");
    }

    [Fact]
    public async Task CorruptIndex_ReturnsFriendlyError_WithValidJson()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Delta.cs"), "class Delta { }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        File.WriteAllBytes(
            Path.Combine(root, ".squeezr", "trigram.idx"),
            Enumerable.Repeat((byte)0xAA, 40).ToArray());

        var search = await RunCliAsync(root, "search", "Delta", "--json");
        search.ExitCode.ShouldBe(1);
        search.StdOut.ShouldBeEmpty();
        var searchError = ParseStdErrError(search.StdErr);
        searchError.GetProperty("code").GetString().ShouldBe("invalid_data");
        searchError.GetProperty("message").GetString()!.ShouldContain("not a squeezr trigram index");

        var compact = await RunCliAsync(root, "compact", "--json");
        compact.ExitCode.ShouldBe(1);
        compact.StdOut.ShouldBeEmpty();
        var compactError = ParseStdErrError(compact.StdErr);
        compactError.GetProperty("code").GetString().ShouldBe("invalid_data");
        compactError.GetProperty("message").GetString()!.ShouldContain("not a squeezr trigram index");
    }

    [Fact]
    public async Task Compact_DropsTombstones_KeepsSearchWorking()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Gamma.cs"), "class Gamma { string Term = \"gammabody\"; }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        File.WriteAllText(Path.Combine(root, "code", "Gamma.cs"), "class Gamma { string Term = \"deltabody\"; }");
        var index = await RunCliAsync(root, "index", "--json");
        index.ExitCode.ShouldBe(0);

        var compact = await RunCliAsync(root, "compact", "--json");
        compact.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(compact.StdOut.Trim()))
        {
            var before = doc.RootElement.GetProperty("sizeBeforeBytes").GetInt64();
            var after = doc.RootElement.GetProperty("sizeAfterBytes").GetInt64();
            after.ShouldBeLessThan(before);
            doc.RootElement.GetProperty("segmentsDropped").GetInt32().ShouldBeGreaterThan(0);
        }

        var search = await RunCliAsync(root, "search", "deltabody", "--json");
        search.ExitCode.ShouldBe(0);
        var searchLines = search.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var doc = JsonDocument.Parse(searchLines[0]))
        {
            doc.RootElement.GetProperty("type").GetString().ShouldBe("match");
        }

        using (var doc = JsonDocument.Parse(searchLines[1]))
        {
            doc.RootElement.GetProperty("type").GetString().ShouldBe("summary");
            doc.RootElement.GetProperty("matches").GetInt32().ShouldBe(1);
        }
    }

    [Fact]
    public async Task Search_QdrantDown_DowngradesUnavailableWarningToFooterHint()
    {
        var root = TestDirs.NewTempDir();
        const string apiKeyEnv = "SQUEEZR_API_KEY_SEARCH_DF5_E2E";

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Omega.cs"), "class Omega { string Term = \"omegabody\"; }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        var config = ConfigFile.Load(root);
        ConfigFile.Save(root, config with
        {
            Embeddings = config.Embeddings with
            {
                Provider = "openai",
                OpenAiCompatible = config.Embeddings.OpenAiCompatible with { ApiKeyEnv = apiKeyEnv }
            },
            Qdrant = config.Qdrant with { Endpoint = "http://localhost:9" }
        });

        var previousKey = Environment.GetEnvironmentVariable(apiKeyEnv);
        Environment.SetEnvironmentVariable(apiKeyEnv, "test-key");
        try
        {
            var first = await RunCliAsync(root, "search", "omegabody");
            var second = await RunCliAsync(root, "search", "omegabody");

            foreach (var run in new[] { first, second })
            {
                run.ExitCode.ShouldBe(0);
                run.StdOut.ShouldContain("Omega.cs");
                run.StdOut.ShouldContain("vector store is unavailable");
                run.StdErr.ShouldNotContain("warning:");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyEnv, previousKey);
        }
    }

    [Fact]
    public async Task Index_TakesOverStaleLock_WarnsOnStderr()
    {
        var root = TestDirs.NewTempDir();

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Kappa.cs"), "class Kappa { }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);

        var deadPid = await ExitedProcessIdAsync();
        File.WriteAllText(
            Path.Combine(SqueezrPaths.StoreDir(root), SqueezrPaths.LockFileName),
            deadPid.ToString());

        var index = await RunCliAsync(root, "index", "--json");

        index.ExitCode.ShouldBe(0);
        index.StdErr.ShouldContain($"warning: stale lock from pid {deadPid} removed");
        File.Exists(Path.Combine(SqueezrPaths.StoreDir(root), SqueezrPaths.LockFileName)).ShouldBeFalse();
    }

    private static async Task<int> ExitedProcessIdAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        await process.WaitForExitAsync();
        return process.Id;
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
