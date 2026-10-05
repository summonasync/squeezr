using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Tui;

// The browser lists the store itself, so the guard has to run where the listing is built: a row is the
// secret's name, and its drill-down is the secret's content read out of meta.db.
public class TuiMapBrowserGuardTests
{
    private const string PemPath = "deploy/keys/monkey.pem";
    private const string PemCanary = "SS2-CANARY-PEM-VALUE";
    private const string HiddenSymbol = "PemKeyFingerprint";

    [Fact]
    public async Task Map_ProtectedRow_BecomesOneGuardLineInsteadOfNames()
    {
        var workspace = NewWorkspaceWithAPlantedSecret();
        var console = WideInteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiMapBrowser.RunAsync(console, workspace, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain(
            "secret guard: 1 result(s) withheld ('deploy/keys/monkey.pem' matches built-in patterns)");

        // every mention of the hidden path rides a guard line: a row would add one more
        CountOccurrences(output, "monkey.pem").ShouldBe(CountOccurrences(output, "secret guard:"));
        CountOccurrences(output, "deploy/").ShouldBe(CountOccurrences(output, "secret guard:"));

        output.ShouldNotContain(HiddenSymbol);
        output.ShouldNotContain(PemCanary);
    }

    [Fact]
    public async Task Map_GuardRow_IsNotNavigable_AndServedFileStillDrills()
    {
        var workspace = NewWorkspaceWithAPlantedSecret();
        var console = WideInteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiMapBrowser.RunAsync(console, workspace, CancellationToken.None);

        console.Output.ShouldContain("App.cs");
        console.Output.ShouldContain("[class] App");
        console.Output.ShouldContain("[method] void App.Run()");
    }

    // the withheld sentence is longer than the default 80-column test console, and a wrapped row would
    // make every substring assertion here about line breaks instead of about the guard
    private static TestConsole WideInteractiveConsole()
    {
        var console = TuiTestHarness.InteractiveConsole();
        console.Width(240);
        return console;
    }

    private static string NewWorkspaceWithAPlantedSecret()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/App.cs", """
            public class App
            {
                public static string Greeting => "SS6-APP-VALUE";

                public void Run()
                {
                    System.Console.WriteLine(Greeting);
                }
            }
            """);
        WriteFile(workspace, $"repo/{PemPath}", $"{PemCanary}\n");
        new WorkspaceIndexer().Index(workspace);

        // ingestion refuses the name now, so the row has to arrive the way a pre-guard store wrote it
        SecretGuardFixture.PlantPreGuardRows(workspace, [PemPath]);
        SecretGuardFixture.PlantSymbol(workspace, PemPath, HiddenSymbol, "variable", "vault seed material");
        return workspace;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
