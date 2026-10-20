using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class RemedyDriftGuardTests
{
    private const string NotInitializedSentence = "Workspace is not initialized. Run 'squeezr init' first.";
    private const string IndexNotBuiltSentence = "Index is not built. Run 'squeezr index' first.";

    private const string ScopeBlindHint =
        "this workspace holds a single repo, but the registry lists other live workspaces: demo — " +
        "a query whose vocabulary belongs to one of them answers with prose noise here, not with not-found; " +
        "remedy: squeezr add <path> --name <repo>, e.g. squeezr add '/work/demo' --name demo, " +
        "or serve there instead: cd '/work/demo' && squeezr serve";

    private static readonly string[] Surfaces =
    [
        "src/Squeezr.Cli/Mcp/ToolErrors.cs",
        "src/Squeezr.Cli/Commands/McpDoctorCommand.cs",
        "src/Squeezr.Cli/Commands/HookPreReadCommand.cs"
    ];

    private static readonly string[] CarrierWiredThrowSites =
    [
        "src/Squeezr.Core/Indexing/WorkspaceCompactor.cs",
        "src/Squeezr.Core/Indexing/VectorPointReconciler.cs"
    ];

    private static readonly string[] InlineRemedyLiterals =
    [
        "squeezr init",
        "squeezr index",
        "squeezr services up",
        "squeezr mcp install",
        "squeezr serve",
        "Run 'squeezr",
        "This workspace is indexed by squeezr",
        "code_search or symbol_lookup"
    ];

    [Fact]
    public void Envelope_IndexNotBuilt_ServesTheCarrierSentenceAndTheAnchoredIndexStep()
    {
        var root = "/work/demo";
        var workspace = new McpWorkspace(root, root, "onnx");
        var ex = Should.Throw<WorkspaceException>(() => NavigationContext.RequireIndex(workspace.Root));

        var payload = Payload(ToolErrors.From(ex, workspace));

        payload.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
        payload.Message.ShouldBe(IndexNotBuiltSentence);
        payload.RootPath.ShouldBe(root);
        payload.NextSteps.ShouldBe([$"cd '{root}' && squeezr index"]);
        payload.Cause.ShouldBeNull();
    }

    [Fact]
    public void Envelope_NotInitialized_ServesTheCarrierSentenceAndTheAnchoredSetupSteps()
    {
        var root = "/work/demo";
        var workspace = new McpWorkspace(root, root, null);

        var payload = Payload(EnvelopeForUninitialized(workspace));

        payload.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        payload.Message.ShouldBe(NotInitializedSentence);
        payload.NextSteps.ShouldBe(
        [
            $"cd '{root}' && squeezr init",
            $"cd '{root}' && squeezr index"
        ]);
        payload.Cause.ShouldBe(ToolErrorCause.RootWithoutManifest);
    }

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("auto", true)]
    [InlineData("onnx", false)]
    [InlineData("openai", false)]
    public void Envelope_IndexNotBuilt_PerProviderStepsComeFromTheCarrier(string provider, bool needsServices)
    {
        var root = "/work/demo";
        var result = ToolErrors.From(
            new WorkspaceException(IndexNotBuiltSentence, WorkspaceException.IndexNotBuiltCode),
            new McpWorkspace(root, root, provider));

        var steps = Payload(result).NextSteps!.ToList();
        if (needsServices)
        {
            steps.ShouldBe(["squeezr services up", $"cd '{root}' && squeezr index"]);
            Payload(result).Cause.ShouldBe(ToolErrorCause.ProviderNeedsServices);
        }
        else
        {
            steps.ShouldBe([$"cd '{root}' && squeezr index"]);
        }
    }

    [Fact]
    public async Task Doctor_UnbuiltIndex_ServesTheSameBlockAsTheEnvelope()
    {
        var workspace = NewWorkspaceWithManifest("onnx");
        var diagnostics = await DoctorAsync(workspace);

        var envelope = Payload(EnvelopeForIndexNotBuilt(workspace, "onnx"));
        var state = diagnostics.IndexState.ShouldNotBeNull();

        state.Code.ShouldBe(envelope.Code);
        state.Message.ShouldBe(envelope.Message);
        state.RootPath.ShouldBe(envelope.RootPath);
        state.NextSteps.ShouldBe(envelope.NextSteps);
        state.Cause.ShouldBe(envelope.Cause);
        state.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
    }

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("auto", true)]
    [InlineData("onnx", false)]
    [InlineData("openai", false)]
    public async Task Doctor_UnbuiltIndex_PerProviderBlockMatchesTheEnvelope(string provider, bool needsServices)
    {
        var workspace = NewWorkspaceWithManifest(provider);
        var diagnostics = await DoctorAsync(workspace);

        var envelope = Payload(EnvelopeForIndexNotBuilt(workspace, provider));
        var state = diagnostics.IndexState.ShouldNotBeNull();

        state.NextSteps.ShouldBe(envelope.NextSteps);
        state.Cause.ShouldBe(envelope.Cause);
        if (needsServices)
        {
            state.Cause.ShouldBe(ToolErrorCause.ProviderNeedsServices);
        }
        else
        {
            state.Cause.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Doctor_UninitializedWorkspace_ServesTheSameBlockAsTheEnvelope()
    {
        var workspace = TestDirs.NewTempDir();
        var diagnostics = await DoctorAsync(workspace);

        var envelope = Payload(EnvelopeForUninitialized(new McpWorkspace(workspace, workspace, null)));
        var state = diagnostics.IndexState.ShouldNotBeNull();

        state.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        state.Code.ShouldBe(envelope.Code);
        state.Message.ShouldBe(NotInitializedSentence);
        state.Message.ShouldBe(envelope.Message);
        state.NextSteps.ShouldBe(envelope.NextSteps);
        state.Cause.ShouldBe(ToolErrorCause.RootWithoutManifest);
    }

    [Fact]
    public async Task Doctor_SessionOutsideRoot_ServesTheRebindBlockAsTheEnvelope()
    {
        var root = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();
        var diagnostics = await DoctorAsync(root, session);

        var envelope = Payload(EnvelopeForUninitialized(new McpWorkspace(root, session, null)));
        var state = diagnostics.IndexState.ShouldNotBeNull();

        state.Code.ShouldBe(envelope.Code);
        state.Message.ShouldBe(envelope.Message);
        state.Cause.ShouldBe(envelope.Cause);
        state.Cause.ShouldBe(ToolErrorCause.SessionOutsideRoot);
        state.NextSteps.ShouldBe(envelope.NextSteps);
        state.NextSteps.ShouldNotBeNull()
            .ShouldNotContain(step => step.Contains(RemedyText.InitCommand, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_WorkspaceIndexed_CannotContradictTheStateBlock()
    {
        var built = NewWorkspaceWithManifest("onnx");
        File.WriteAllText(SqueezrPaths.TrigramIndexPath(built), "not-a-real-index");
        (await DoctorAsync(built)).WorkspaceIndexed.ShouldBeTrue();

        var uninitialized = await DoctorAsync(TestDirs.NewTempDir());
        uninitialized.IndexState.ShouldNotBeNull();
        uninitialized.WorkspaceIndexed.ShouldBeFalse();

        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName), string.Empty);
        var outside = await DoctorAsync(root, TestDirs.NewTempDir());
        outside.IndexState.ShouldNotBeNull();
        outside.IndexState!.Cause.ShouldBe(ToolErrorCause.SessionOutsideRoot);
        outside.WorkspaceIndexed.ShouldBeFalse();
    }

    [Fact]
    public void Doctor_Command_WiresTheSessionDirectoryIntoTheEnvironment()
    {
        ReadSource("src/Squeezr.Cli/Commands/McpDoctorCommand.cs").ShouldContain(
            "WorkspaceRootResolver.LaunchDirectory()",
            customMessage: "doctor must hand the session directory to the classification, " +
                           "otherwise the outside-root block is dead code");
    }

    [Fact]
    public async Task Doctor_BuiltIndex_OmitsTheStateBlock()
    {
        var workspace = NewWorkspaceWithManifest("onnx");
        File.WriteAllText(SqueezrPaths.TrigramIndexPath(workspace), "not-a-real-index");

        var diagnostics = await DoctorAsync(workspace);

        diagnostics.IndexState.ShouldBeNull();
        diagnostics.WorkspaceIndexed.ShouldBeTrue();
    }

    [Fact]
    public void Diagnostics_JsonBlock_CarriesTheEnvelopeFieldNames()
    {
        var state = new ToolErrorPayload(
            WorkspaceException.IndexNotBuiltCode,
            IndexNotBuiltSentence,
            "/work/demo",
            ["cd '/work/demo' && squeezr index"],
            null);
        var diagnostics = new McpClientDiagnostics(
            "kimi", true, false, false, "writer", "writer", true, true, null, [],
            IndexState: state);

        var json = JsonSerializer.Serialize(diagnostics, SqueezrJson.OutputOptions);

        json.ShouldContain("\"indexState\":{");
        json.ShouldContain("\"code\":\"index_not_built\"");
        json.ShouldContain("\"nextSteps\":[");
        json.ShouldContain("\"message\"");
        json.ShouldContain("\"rootPath\"");
    }

    [Fact]
    public void Surfaces_InlineNoRemedyLiterals_EveryWordingComesFromTheCarrier()
    {
        var drifted = Surfaces
            .Select(ReadSource)
            .SelectMany(source => InlineRemedyLiterals
                .Where(literal => source.Contains(literal, StringComparison.Ordinal))
                .Select(literal => literal))
            .ToList();

        drifted.ShouldBeEmpty();
    }

    [Fact]
    public void Surfaces_ReferenceTheCarrier()
    {
        foreach (var surface in Surfaces)
        {
            ReadSource(surface).ShouldContain(
                "RemedyText", customMessage: $"the {surface} surface must serve its remedy wording from RemedyText");
        }
    }

    [Fact]
    public void ThrowSitesThatUsedToCarryTheirOwnSentence_WireTheCarrier()
    {
        var drifted = CarrierWiredThrowSites
            .Select(site => (Site: site, Source: ReadSource(site)))
            .SelectMany(pair => pair.Source.Contains("RemedyText.", StringComparison.Ordinal)
                ? InlineRemedyLiterals
                    .Where(literal => pair.Source.Contains(literal, StringComparison.Ordinal))
                    .Select(literal => $"{pair.Site}: {literal}")
                : [$"{pair.Site}: does not quote the carrier"])
            .ToList();

        drifted.ShouldBeEmpty();
    }

    [Fact]
    public void Carrier_ScopeBlindHint_ServesThePinnedText()
    {
        var hint = RemedyText.ScopeBlindSiblings(
            [new WorkspaceSiblingEntry("demo", "/work/demo", "demo-store", Current: false, Stale: false)]);

        hint.ShouldBe(ScopeBlindHint);
    }

    [Fact]
    public void Carrier_ScopeBlindHint_NamesAtMostThreeSiblingsAndNeverSuggestsInit()
    {
        var siblings = Enumerable.Range(0, 5)
            .Select(index => new WorkspaceSiblingEntry(
                $"sib-{index}", $"/work/sib-{index}", $"sk-{index}", Current: false, Stale: false))
            .ToList();

        var hint = RemedyText.ScopeBlindSiblings(siblings);

        hint.ShouldNotBeNull();
        hint.ShouldContain("sib-0, sib-1, sib-2");
        hint.ShouldNotContain("sib-3");
        hint.ShouldNotContain("sib-4");
        hint.ShouldNotContain("squeezr init");
    }

    [Fact]
    public void Carrier_ScopeBlindHint_WithoutLiveSiblings_RendersNothing()
    {
        RemedyText.ScopeBlindSiblings([]).ShouldBeNull();
    }

    [Fact]
    public void Carrier_ScopeBlindHint_IndexStatusToolWiresTheCarrier()
    {
        ReadSource("src/Squeezr.Cli/Mcp/Tools/IndexStatusTool.cs").ShouldContain(
            "RemedyText.ScopeBlindSiblings",
            customMessage: "the scope-blind hint must serve its wording from RemedyText");
    }

    private static CallToolResult EnvelopeForUninitialized(McpWorkspace workspace)
    {
        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);
        return ToolErrors.From(ex, workspace);
    }

    private static CallToolResult EnvelopeForIndexNotBuilt(string root, string provider)
    {
        var workspace = new McpWorkspace(root, root, provider);
        var ex = Should.Throw<WorkspaceException>(() => NavigationContext.RequireIndex(workspace.Root));
        return ToolErrors.From(ex, workspace);
    }

    private static async Task<McpClientDiagnostics> DoctorAsync(string workspace, string? session = null)
    {
        var environment = new McpOnboardingEnvironment(
            TestDirs.NewTempDir(), workspace, TestDirs.NewTempDir(), null, session);
        var service = new McpOnboardingService(new ClientCliLocator(string.Empty));

        return await service.DoctorAsync(
            McpClientRegistry.Get(McpClientId.Kimi), environment, CancellationToken.None);
    }

    private static string NewWorkspaceWithManifest(string provider)
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);
        ConfigFile.Save(root, ConfigFile.Load(root) with
        {
            Embeddings = new EmbeddingsConfig { Provider = provider }
        });
        return root;
    }

    private static ToolErrorPayload Payload(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().Deserialize<ToolErrorPayload>()!;

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
