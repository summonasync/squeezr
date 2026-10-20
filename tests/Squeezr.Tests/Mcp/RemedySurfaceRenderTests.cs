using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

[Collection("CliProcessGlobals")]
public sealed class RemedySurfaceRenderTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string Client = "kimi";
    private const string Root = "/work/demo";

    private const string Nudge =
        "This workspace is indexed by squeezr. For a read this size, code_search or symbol_lookup can locate " +
        "the relevant lines, and expand_span(file, startLine, endLine) returns just that span from the live file. " +
        "If the file or repo is not in the index, a plain Read is correct.";

    private readonly string? _previousHome;
    private readonly string _home;

    public RemedySurfaceRenderTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void Hook_BulkRead_ServesTheCarrierNudgeVerbatim()
    {
        var workspace = TestDirs.NewTempDir();
        var canonical = WorkspaceCanonicalPath.Canonicalize(workspace);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "remedy-hook", canonical, WorkspaceStoreKeys.Create("remedy-hook", canonical),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var payload = JsonSerializer.Serialize(new HookPreReadInput
        {
            ToolName = "Read",
            ToolInput = new HookPreReadToolInput { FilePath = Path.Combine(workspace, "big.cs"), Limit = 500 }
        });

        var advice = HookPreReadCommand.Evaluate(payload);

        var context = advice.ShouldNotBeNull().HookSpecificOutput.AdditionalContext;
        context.ShouldBe(Nudge);
    }

    [Fact]
    public void Doctor_Render_UnbuiltIndex_PrintsTheEnvelopeStepsAsOneRemedyLine()
    {
        var diagnostics = Diagnostics(indexState: new ToolErrorPayload(
            WorkspaceException.IndexNotBuiltCode,
            RemedyText.IndexNotBuilt,
            Root,
            ["squeezr services up", $"cd '{Root}' && squeezr index"],
            ToolErrorCause.ProviderNeedsServices));

        var output = Render(diagnostics);

        output.ShouldContain($"remedy: squeezr services up && cd '{Root}' && squeezr index");
    }

    [Fact]
    public void Doctor_Render_OutsideRoot_PrintsTheRebindRemedyInsteadOfInit()
    {
        var workspace = new McpWorkspace(Root, "/work/elsewhere", null);
        var diagnostics = Diagnostics(indexState: ToolErrors.PayloadFor(
            workspace.NotInitializedException(), workspace));

        var output = Render(diagnostics);

        output.ShouldContain(
            $"remedy: {RemedyText.RebindInstall("/work/elsewhere")} && {RemedyText.RebindServe("/work/elsewhere")}");
        output.ShouldNotContain(RemedyText.InitCommand);
    }

    [Fact]
    public void Doctor_Render_IndexedWorkspace_PrintsNoRemedyLine()
    {
        var output = Render(Diagnostics(indexState: null));

        output.ShouldNotContain("remedy:");
    }

    [Fact]
    public void Doctor_Render_StaleEntry_RepairsWithTheCarrierCommand()
    {
        var diagnostics = Diagnostics(
            indexState: null,
            userStale: true,
            userCommand: "/opt/old-version/squeezr");

        var output = Render(diagnostics);

        output.ShouldContain($"repair: squeezr mcp install --client {Client} --yes");
    }

    [Fact]
    public void CliError_Json_IndexNotBuilt_CarriesTheEnvelopeNextSteps()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(stderr);
        try
        {
            CommandError.Report(
                new WorkspaceException(RemedyText.IndexNotBuilt, WorkspaceException.IndexNotBuiltCode),
                json: true);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var body = JsonDocument.Parse(stderr.ToString().Trim()).RootElement.GetProperty("error");
        body.GetProperty("code").GetString().ShouldBe(WorkspaceException.IndexNotBuiltCode);
        var steps = body.GetProperty("nextSteps");
        steps.GetArrayLength().ShouldBe(1);
        steps[0].GetString().ShouldBe($"cd '{Directory.GetCurrentDirectory()}' && squeezr index");
    }

    [Fact]
    public void CliError_Json_CodeWithoutRemedy_OmitsNextSteps()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(stderr);
        try
        {
            CommandError.Report(new WorkspaceException("Bad query.", WorkspaceException.InvalidQueryCode), json: true);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var body = JsonDocument.Parse(stderr.ToString().Trim()).RootElement.GetProperty("error");
        body.TryGetProperty("nextSteps", out _).ShouldBeFalse();
    }

    private static McpClientDiagnostics Diagnostics(
        ToolErrorPayload? indexState,
        bool userStale = false,
        string? userCommand = null) => new(
        Client,
        Installed: true,
        UserScopeEntry: true,
        ProjectScopeEntry: false,
        UserScopeMethod: "cli",
        ProjectScopeMethod: "writer",
        SqueezrInPath: true,
        WorkspaceIndexed: indexState is null,
        HeadlessStatus: null,
        Warnings: [],
        UserCommand: userCommand,
        ProjectCommand: null,
        UserStale: userStale,
        ProjectStale: false,
        ExpectedCommand: userStale ? StableExpectedCommand() : null,
        IndexState: indexState);

    private static string StableExpectedCommand() => Path.Combine("/expected", "squeezr");

    private static string Render(params McpClientDiagnostics[] diagnostics)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Width = 400;
        var previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            McpDoctorCommand.Render(diagnostics);
        }
        finally
        {
            AnsiConsole.Console = previousConsole;
        }

        return console.Output;
    }
}
