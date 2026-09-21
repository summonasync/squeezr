using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public class SearchExpandContractTests
{
    [Fact]
    public async Task Search_VerbatimPath_Expands_InPrettyAndJsonForms()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/Calc.cs", """
            public class Calc
            {
                // needlestring lives here
                public int Add(int a, int b) => a + b;

                public int Subtract(int a, int b) => a - b;
            }
            """);

        (await RunCliAsync(root, "init", "--name", "contract", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "add", ".", "--name", "dot", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        var pretty = await RunCliAsync(root, "search", "needlestring");
        pretty.ExitCode.ShouldBe(0);
        var prettyLine = pretty.StdOut.Trim().Split('\n')[0];
        var prettyPath = prettyLine[..prettyLine.IndexOf(':')];
        prettyPath.ShouldContain("dot/src/Calc.cs");

        var expandPretty = await RunCliAsync(root, "expand", prettyPath, "1", "5");
        expandPretty.ExitCode.ShouldBe(0);
        expandPretty.StdOut.ShouldContain("needlestring");

        var json = await RunCliAsync(root, "search", "needlestring", "--json");
        json.ExitCode.ShouldBe(0);
        string jsonPath;
        using (var doc = JsonDocument.Parse(json.StdOut.Trim().Split('\n')[0]))
        {
            jsonPath = doc.RootElement.GetProperty("path").GetString()!;
        }

        jsonPath.ShouldBe("src/Calc.cs");
        var expandJson = await RunCliAsync(root, "expand", jsonPath, "1", "5", "--json");
        expandJson.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(expandJson.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("startLine").GetInt32().ShouldBe(1);
            doc.RootElement.GetProperty("endLine").GetInt32().ShouldBe(5);
        }
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
