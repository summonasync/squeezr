using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

public sealed class CliEndToEndTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "Squeezr.Cli.dll");

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

        var search = await RunCliAsync(root, "search", "Alpha", "--json");
        search.ExitCode.ShouldBe(0);

        using (var doc = JsonDocument.Parse(search.StdOut.Trim()))
        {
            var matches = doc.RootElement.GetProperty("matches");
            matches.GetArrayLength().ShouldBe(1);
            matches[0].GetProperty("path").GetString().ShouldBe("Alpha.cs");
            matches[0].GetProperty("repo").GetString().ShouldBe("code");
            matches[0].GetProperty("line").GetInt32().ShouldBe(1);
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
            doc.RootElement.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        }

        var bare = TestDirs.NewTempDir();
        var missing = await RunCliAsync(bare, "search", "Alpha", "--json");
        missing.ExitCode.ShouldBe(1);
        missing.StdErr.ShouldContain("squeezr index");

        var invalid = await RunCliAsync(root, "search", "[", "--regex", "--json");
        invalid.ExitCode.ShouldBe(1);
        invalid.StdErr.ShouldContain("Invalid regex pattern");
        using (var doc = JsonDocument.Parse(invalid.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("Invalid regex pattern");
        }
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
        search.StdErr.ShouldContain("error:");
        using (var doc = JsonDocument.Parse(search.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("not a squeezr trigram index");
        }

        var compact = await RunCliAsync(root, "compact", "--json");
        compact.ExitCode.ShouldBe(1);
        compact.StdErr.ShouldContain("error:");
        using (var doc = JsonDocument.Parse(compact.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("not a squeezr trigram index");
        }
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
        using (var doc = JsonDocument.Parse(search.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("matches").GetArrayLength().ShouldBe(1);
        }
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
