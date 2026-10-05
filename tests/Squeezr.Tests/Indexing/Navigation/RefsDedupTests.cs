using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("CliProcessGlobals")]
public class RefsDedupTests
{
    [Fact]
    public void Find_DefinitionsSharingOneRefSet_CollapseIntoOneBlock()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();

        var result = new RefLookup(workspace).Find(RefDedupFixture.Symbol, null);

        var block = result.Groups.ShouldHaveSingleItem();
        block.Definitions.Select(d => d.File).ShouldBe(["src/IMaterializer.cs", "src/Materializer.cs"]);
        block.Refs.Count.ShouldBe(3);
        Rows(result).ShouldBe(RefDedupFixture.Callsites);
    }

    [Fact]
    public void Find_DefinitionsWithDifferentRefSets_KeepTheirOwnBlocks()
    {
        var workspace = RefDedupFixture.NewOverloadWorkspace();

        var result = new RefLookup(workspace).Find("Add", null);

        result.Groups.Count.ShouldBe(2);
        result.Groups.ShouldAllBe(g => g.Definitions.ShouldHaveSingleItem().File == "src/Calc.cs");
        result.Groups.Select(g => g.Definitions[0].StartLine).Distinct().Count().ShouldBe(2);
        Rows(result).ShouldBe(["src/Runner.cs:7", "src/Other.cs:7"]);
    }

    [Fact]
    public void Find_RowCapturedTwiceForOneLocation_IsReportedOnce()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        RefDedupFixture.PlantDuplicateRow(workspace, "demo", "src/Guard.cs", 7);

        var result = new RefLookup(workspace).Find(RefDedupFixture.Symbol, null);

        Rows(result).ShouldBe(RefDedupFixture.Callsites);
        result.Groups.ShouldHaveSingleItem().Definitions.Count.ShouldBe(2);
    }

    [Fact]
    public void Find_MirroredRepositories_KeepTheirOwnBlocks()
    {
        var workspace = RefDedupFixture.NewMirroredWorkspace();

        var result = new RefLookup(workspace).Find(RefDedupFixture.Symbol, null);

        result.Groups.Select(g => g.Repo).ShouldBe(["alpha", "beta"]);
        result.Groups.ShouldAllBe(g => g.Definitions.Count == 2 && g.Refs.Count == 3);
        Rows(result).ShouldBe(
        [
            "src/Guard.cs:7", "src/Guard.cs:8", "src/Reporter.cs:7",
            "src/Guard.cs:7", "src/Guard.cs:8", "src/Reporter.cs:7",
        ]);
    }

    [Fact]
    public void Find_PlantInNamedRepository_ExtendsOnlyThatRepository()
    {
        var workspace = RefDedupFixture.NewMirroredWorkspace();
        RefDedupFixture.PlantExtraRow(workspace, "beta", "src/Materializer.cs", "src/Guard.cs", 12);

        var result = new RefLookup(workspace).Find(RefDedupFixture.Symbol, null);

        LabeledRows(result).ShouldBe(
        [
            "alpha:src/Guard.cs:7", "alpha:src/Guard.cs:8", "alpha:src/Reporter.cs:7",
            "beta:src/Guard.cs:7", "beta:src/Guard.cs:8", "beta:src/Reporter.cs:7", "beta:src/Guard.cs:12",
        ]);
        result.Groups.ShouldNotContain(g => g.Repo == "alpha" && g.Refs.Any(r => r.Line == 12));
    }

    [Fact]
    public void Find_ProtectedDefinitionWithPublicTwin_WithholdsTheDeclaration()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "Greeting", "method");

        var result = new RefLookup(workspace).Find("Greeting", null);

        result.Groups.ShouldNotBeEmpty();
        result.Groups.SelectMany(g => g.Definitions).ShouldContain(d => d.File == SecretGuardFixture.AppPath);
        result.Groups.SelectMany(g => g.Definitions).ShouldNotContain(d => d.File == SecretGuardFixture.EnvPath);
        result.Groups.SelectMany(g => g.Refs).ShouldNotContain(r => r.File == SecretGuardFixture.EnvPath);
    }

    [Fact]
    public async Task Refs_PartiallyOverlappingBlocks_RepeatPerBlockAndCollapseInFlatLists()
    {
        var root = RefDedupFixture.NewInterfaceAndImplWorkspace();
        RefDedupFixture.PlantExtraRow(root, "demo", "src/Materializer.cs", "src/Guard.cs", 12);

        var result = new RefLookup(root).Find(RefDedupFixture.Symbol, null);
        result.Groups.Count.ShouldBe(2);
        result.Groups.Select(g => g.Refs.Count).ShouldBe([3, 4]);

        var pretty = await RunCliAsync(root, "refs", RefDedupFixture.Symbol);
        pretty.ExitCode.ShouldBe(0);
        CountOccurrences(pretty.StdOut, "src/Guard.cs:7 call").ShouldBe(2);

        var json = await RunCliAsync(root, "refs", RefDedupFixture.Symbol, "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.GetProperty("refs").EnumerateArray()
            .Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(["src/Guard.cs:7", "src/Guard.cs:8", "src/Reporter.cs:7", "src/Guard.cs:12"]);
    }

    [Fact]
    public async Task Refs_Cli_MirroredRepositories_KeepBothCallsiteSets()
    {
        var root = RefDedupFixture.NewMirroredWorkspace();

        var json = await RunCliAsync(root, "refs", RefDedupFixture.Symbol, "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.GetProperty("refs").EnumerateArray()
            .Select(r => $"{r.GetProperty("repo").GetString()}:{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(
            [
                "alpha:src/Guard.cs:7", "alpha:src/Guard.cs:8", "alpha:src/Reporter.cs:7",
                "beta:src/Guard.cs:7", "beta:src/Guard.cs:8", "beta:src/Reporter.cs:7",
            ]);
        doc.RootElement.GetProperty("definitions").EnumerateArray()
            .Select(d => d.GetProperty("repo").GetString())
            .ShouldBe(["alpha", "alpha", "beta", "beta"]);

        var pretty = await RunCliAsync(root, "refs", RefDedupFixture.Symbol);
        CountOccurrences(pretty.StdOut, "src/Guard.cs:7 call").ShouldBe(2);
    }

    [Fact]
    public async Task Refs_Cli_ProtectedDefinition_NeverNamesTheSecret()
    {
        var root = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(root, SecretGuardFixture.EnvPath, "Greeting", "method");
        SecretGuardFixture.PlantSymbol(root, SecretGuardFixture.ExamplePath, "Greeting", "method");

        var json = await RunCliAsync(root, "refs", "Greeting", "--json");
        json.ExitCode.ShouldBe(0);
        // the quoted needle is the whole path: a bare '.env' substring would also catch the public '.env.example'
        json.StdOut.ShouldNotContain($"\"{SecretGuardFixture.EnvPath}\"");
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        var definitions = doc.RootElement.GetProperty("definitions").EnumerateArray()
            .Select(d => d.GetProperty("file").GetString()!)
            .ToList();
        definitions.ShouldNotContain(SecretGuardFixture.EnvPath);
        definitions.ShouldContain(SecretGuardFixture.ExamplePath);
        doc.RootElement.GetProperty("refs").EnumerateArray()
            .Select(r => r.GetProperty("file").GetString())
            .ShouldNotContain(SecretGuardFixture.EnvPath);
    }

    [Fact]
    public async Task Refs_Cli_SharedRefSets_PrintEveryRowOnce()
    {
        var root = RefDedupFixture.NewInterfaceAndImplWorkspace();

        var pretty = await RunCliAsync(root, "refs", RefDedupFixture.Symbol);
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("2 definitions");
        pretty.StdOut.ShouldContain("def method demo:src/IMaterializer.cs:");
        pretty.StdOut.ShouldContain("def method demo:src/Materializer.cs:");
        foreach (var callsite in RefDedupFixture.Callsites)
        {
            CountOccurrences(pretty.StdOut, $"{callsite} call").ShouldBe(1);
        }

        var json = await RunCliAsync(root, "refs", RefDedupFixture.Symbol, "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.GetProperty("refs").EnumerateArray()
            .Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(RefDedupFixture.Callsites);
        doc.RootElement.GetProperty("definitions").EnumerateArray()
            .Select(d => d.GetProperty("file").GetString())
            .ShouldBe(["src/IMaterializer.cs", "src/Materializer.cs"]);
    }

    [Fact]
    public async Task Refs_Cli_SingleDefinition_KeepsTheOneLineHeader()
    {
        var root = RefDedupFixture.NewInterfaceAndImplWorkspace();

        var pretty = await RunCliAsync(root, "refs", "IMaterializer");
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("demo:src/IMaterializer.cs:");
        pretty.StdOut.ShouldNotContain("definitions,");
        pretty.StdOut.ShouldNotContain("  def ");
    }

    private static List<string> Rows(RefLookupResult result) =>
        [.. result.Groups.SelectMany(g => g.Refs).Select(r => $"{r.File}:{r.Line}")];

    private static List<string> LabeledRows(RefLookupResult result) =>
        [.. result.Groups.SelectMany(g => g.Refs.Select(r => $"{g.Repo}:{r.File}:{r.Line}")).Distinct()];

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
