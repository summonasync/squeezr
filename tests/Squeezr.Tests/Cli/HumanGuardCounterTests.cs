using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Cli;

// The human half of the read guard: --json has carried the withheld tally since T3, and the terminal
// renderings had to stop answering a truncated question as if it were a complete one.
[Collection("ProcessGlobals")]
public sealed class HumanGuardCounterTests
{
    private static readonly Regex CounterLine = new(
        @"^warning: secret guard: \d+ result\(s\) withheld \('.+ matches (built-in patterns|indexing\.secretGuardExtra)\)$",
        RegexOptions.Compiled);

    [Fact]
    public async Task Map_Human_PrefixesTheWithheldTallyAndRowsNoSecret()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var run = await CliTestProcess.RunAsync(workspace, "map", "--depth", "3");

        run.ExitCode.ShouldBe(0);
        GuardLines(run.StdErr).ShouldNotBeEmpty();
        run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ShouldNotContain(".env");
        run.StdOut.ShouldNotContain("monkey.pem");
        run.StdOut.ShouldNotContain("secrets.json");
        run.StdOut.ShouldContain(".env.example");
        run.StdOut.ShouldContain("App.cs");
        NoCanaryValue(run.StdOut + run.StdErr);
    }

    [Fact]
    public async Task Symbol_Human_TallyRidesTheNotFoundHint()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");

        var run = await CliTestProcess.RunAsync(workspace, "symbol", "OPENAI_API_KEY");

        run.ExitCode.ShouldBe(0);
        GuardLines(run.StdErr).ShouldNotBeEmpty();
        run.StdOut.ShouldNotContain(SecretGuardFixture.EnvCanary);
        NoCanaryValue(run.StdOut + run.StdErr);
    }

    [Fact]
    public async Task Refs_Human_TallyRidesTheRenderedBlocks()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);

        var run = await CliTestProcess.RunAsync(workspace, "refs", "Run");

        run.ExitCode.ShouldBe(0);
        GuardLines(run.StdErr).ShouldNotBeEmpty();
        run.StdOut.ShouldNotContain($"{SecretGuardFixture.EnvPath}:");
        run.StdOut.ShouldContain("src/App.cs");
        NoCanaryValue(run.StdOut + run.StdErr);
    }

    [Fact]
    public async Task ThreeSurfaces_DenyTheSameSentence()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);

        var shapes = new List<string>();
        var surfaces = new[]
        {
            new[] { "map", "--depth", "3" },
            new[] { "symbol", "OPENAI_API_KEY" },
            new[] { "refs", "Run" },
        };
        foreach (var args in surfaces)
        {
            var run = await CliTestProcess.RunAsync(workspace, args);
            var line = GuardLines(run.StdErr).ShouldHaveSingleItem();
            shapes.Add(Regex.Replace(line, @"secret guard: \d+", "secret guard: N"));
        }

        shapes.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task HumanSurfaces_StaysSilentWhenNothingWasWithheld()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var clean = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(clean, "repo"));
        File.WriteAllText(Path.Combine(clean, "repo", "Plain.cs"), "public class Plain { }\n");
        (await CliTestProcess.RunAsync(clean, "init", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(clean, "add", "repo", "--name", "plain", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(clean, "index", "--json")).ExitCode.ShouldBe(0);

        var cleanRun = await CliTestProcess.RunAsync(clean, "map");
        var servedRun = await CliTestProcess.RunAsync(workspace, "symbol", "App");
        var guardedRun = await CliTestProcess.RunAsync(workspace, "map", "--depth", "3");

        cleanRun.ExitCode.ShouldBe(0);
        cleanRun.StdErr.ShouldNotContain("secret guard");
        servedRun.StdErr.ShouldNotContain("secret guard");
        servedRun.StdOut.ShouldContain("App");
        GuardLines(guardedRun.StdErr).ShouldNotBeEmpty();
    }

    private static IReadOnlyList<string> GuardLines(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => CounterLine.IsMatch(line))
            .ToList();

    private static void NoCanaryValue(string output)
    {
        foreach (var canary in SecretGuardFixture.ProtectedCanaries)
        {
            output.ShouldNotContain(canary);
        }
    }
}
