using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SymbolMapExpandTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "Squeezr.Cli.dll");

    [Fact]
    public void Symbol_ExactName_ReturnsSignatureSpanDocAndBody()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Add", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.Name.ShouldBe("Add");
        hit.Kind.ShouldBe("method");
        hit.File.ShouldBe("src/Calc.cs");
        hit.Signature!.ShouldContain("Add(int a, int b)");
        hit.DocComment!.ShouldContain("Adds numbers.");
        hit.Body!.ShouldContain("return a + b;");
        result.TokensUsed.ShouldBeLessThanOrEqualTo(SymbolLookup.DefaultMaxTokens);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Symbol_AmbiguousName_ListsCandidatesWithoutBodies()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Name", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.Count.ShouldBe(2);
        result.Matches.Select(m => m.File).OrderBy(p => p).ShouldBe(["src/Gadget.cs", "src/Widget.cs"]);
        result.Matches.ShouldAllBe(m => m.Body == null);
    }

    [Fact]
    public void Symbol_KindFilter_MatchesOrThrowsWhenNothingFits()
    {
        var workspace = NewIndexedWorkspace();
        var lookup = new SymbolLookup(workspace);

        lookup.Find("Area", null, "property", 300, body: false).Matches.ShouldHaveSingleItem();

        var ex = Should.Throw<WorkspaceException>(() =>
            lookup.Find("Calc", null, "interface", 300, body: false));
        ex.Message.ShouldContain("No symbol named 'Calc'");
    }

    [Fact]
    public void Symbol_TinyBudget_TruncatesWithHint()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Calc", null, null, 15, body: false);

        result.Truncated.ShouldBeTrue();
        result.Hint.ShouldNotBeNull();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(15);
    }

    [Fact]
    public void Map_DefaultDepth_ShowsTypesButNotMembers()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, RepoMapper.DefaultDepth, RepoMapper.DefaultMaxTokens);

        result.Truncated.ShouldBeFalse();
        var text = string.Join("\n", result.Lines.Select(l => l.Text));
        text.ShouldContain("Calc");
        text.ShouldNotContain("Add(int a, int b)");
        text.ShouldContain("util.js");
    }

    [Fact]
    public void Map_DepthThree_IncludesMemberSignatures()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, RepoMapper.DefaultMaxTokens);

        var text = string.Join("\n", result.Lines.Select(l => l.Text));
        text.ShouldContain("Calc.Add(int a, int b)");
    }

    [Fact]
    public void Map_TinyBudget_TruncatesWithinBudget()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 2, 20);

        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(30);
        result.Hint.ShouldNotBeNull();
    }

    [Fact]
    public void Expand_ReturnsSpanWithContext()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand("src/Calc.cs", 8, 8, 2, null, 1000);

        result.Span.ShouldHaveSingleItem().ShouldContain("return a + b;");
        result.Before.ShouldBe(["    public int Add(int a, int b)", "    {"]);
        result.After.ShouldBe(["    }", ""]);
        result.StartLine.ShouldBe(8);
        result.EndLine.ShouldBe(8);
    }

    [Fact]
    public void Expand_ClampsOutOfRangeLinesToFileBounds()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand("src/Calc.cs", 1, 999, 0, null, 1000);

        result.StartLine.ShouldBe(1);
        result.EndLine.ShouldBe(15);
        result.Span.Count.ShouldBe(15);
        result.Before.ShouldBeEmpty();
        result.After.ShouldBeEmpty();
    }

    [Fact]
    public async Task Expand_DuplicatePathAcrossRepos_ErrorsNamingBothRepos()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo-a/src/Dup.cs", "public class Alpha { }\n");
        WriteFile(workspace, "repo-b/src/Dup.cs", "public class Beta { }\n");
        manager.AddRepo("repo-a", "alpha");
        manager.AddRepo("repo-b", "beta");
        new WorkspaceIndexer().Index(workspace);

        var ex = Should.Throw<WorkspaceException>(() =>
            new SpanExpander(workspace).Expand("src/Dup.cs", 1, null, 3, null, 100));
        ex.Message.ShouldContain("alpha:src/Dup.cs");
        ex.Message.ShouldContain("beta:src/Dup.cs");
        ex.Message.ShouldContain("--repo");

        var qualified = new SpanExpander(workspace).Expand("src/Dup.cs", 1, 1, 0, "beta", 100);
        qualified.Repo.ShouldBe("beta");

        var cli = await RunCliAsync(workspace, "expand", "src/Dup.cs", "1", "3", "--json");
        cli.ExitCode.ShouldBe(1);
        using (var doc = JsonDocument.Parse(cli.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("alpha:src/Dup.cs");
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("beta:src/Dup.cs");
        }

        var cliQualified = await RunCliAsync(workspace, "expand", "src/Dup.cs", "1", "1", "--repo", "beta", "--json");
        cliQualified.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void AllCommands_MissingIndexMessage()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);

        Should.Throw<WorkspaceException>(() => new SymbolLookup(workspace).Find("Add", null, null, 300, false))
            .Message.ShouldContain("Run 'squeezr index'");
        Should.Throw<WorkspaceException>(() => new RepoMapper(workspace).Build(null, 2, 100))
            .Message.ShouldContain("Run 'squeezr index'");
        Should.Throw<WorkspaceException>(() => new SpanExpander(workspace).Expand("x.cs", 1, null, 3, null, 100))
            .Message.ShouldContain("Run 'squeezr index'");
    }

    [Fact]
    public async Task Cli_E2E_SymbolMapExpandJson_IsValid()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "repo", "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "repo", "src", "Calc.cs"), """
            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);

        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "add", "repo", "--name", "demo", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        var symbol = await RunCliAsync(root, "symbol", "Add", "--json");
        symbol.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(symbol.StdOut.Trim()))
        {
            var match = doc.RootElement.GetProperty("matches")[0];
            match.GetProperty("signature").GetString()!.ShouldContain("Add(int a, int b)");
            match.GetProperty("file").GetString().ShouldBe("src/Calc.cs");
            doc.RootElement.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        }

        var map = await RunCliAsync(root, "map", "--json");
        map.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(map.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("lines").GetArrayLength().ShouldBeGreaterThan(0);
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        }

        var mapTruncated = await RunCliAsync(root, "map", "--max-tokens", "4", "--json");
        using (var doc = JsonDocument.Parse(mapTruncated.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        }

        var expand = await RunCliAsync(root, "expand", "src/Calc.cs", "6", "8", "--json");
        expand.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(expand.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("startLine").GetInt32().ShouldBe(6);
            doc.RootElement.GetProperty("endLine").GetInt32().ShouldBe(8);
            doc.RootElement.GetProperty("span").GetArrayLength().ShouldBe(3);
        }

        var missing = await RunCliAsync(root, "symbol", "Nope", "--json");
        missing.ExitCode.ShouldBe(1);
        using (var doc = JsonDocument.Parse(missing.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("No symbol named 'Nope'");
        }
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Widget.cs", """
            namespace Demo;

            public class Widget
            {
                public string Name => "w";

                public int Area => 4;
            }
            """);
        WriteFile(workspace, "repo/src/Gadget.cs", """
            namespace Demo;

            public class Gadget
            {
                public string Name => "g";
            }
            """);
        WriteFile(workspace, "repo/lib/util.js", "function helper() { return 1; }\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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
