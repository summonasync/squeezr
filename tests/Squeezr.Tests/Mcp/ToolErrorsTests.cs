using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class ToolErrorsTests
{
    private const string RootPath = "/work/demo";
    private const string InsideSession = "/work/demo/src/Squeezr.Core";
    private const string OutsideSession = "/other/project";
    private const string NotInitializedMessage = "Workspace is not initialized. Run 'squeezr init' first.";
    private const string IndexNotBuiltMessage = "Index is not built. Run 'squeezr index' first.";

    [Fact]
    public void From_NotInitialized_CarriesCodeRootPathCauseAndNonEmptyNextSteps()
    {
        var result = From(NotInitializedMessage, WorkspaceException.NotInitializedCode);

        var payload = Payload(result);
        payload.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        payload.RootPath.ShouldBe(RootPath);
        payload.Cause.ShouldBe(ToolErrorCause.RootWithoutManifest);
        payload.NextSteps.ShouldNotBeEmpty();
    }

    [Fact]
    public void From_NotInitialized_TextBlockStaysTheOriginalSentenceAlone()
    {
        var result = From(NotInitializedMessage, WorkspaceException.NotInitializedCode);

        result.IsError.ShouldBe(true);
        result.Content.ShouldHaveSingleItem()
            .ShouldBeOfType<TextContentBlock>().Text.ShouldBe(NotInitializedMessage);
    }

    [Theory]
    [InlineData(WorkspaceException.NotInitializedCode)]
    [InlineData(WorkspaceException.IndexNotBuiltCode)]
    public void From_SetupCode_NextStepsAreExactCommands(string code)
    {
        var payload = Payload(From("Whatever.", code));

        payload.NextSteps.ShouldNotBeEmpty();
        payload.NextSteps!.ShouldAllBe(step => step.Contains("squeezr ", StringComparison.Ordinal));
    }

    [Fact]
    public void From_NotInitializedSessionAtRoot_StepsNameTheRootAndNeverMentionRegistration()
    {
        var result = From(NotInitializedMessage, WorkspaceException.NotInitializedCode);

        var steps = Payload(result).NextSteps!.ToList();
        steps[0].ShouldBe($"cd '{RootPath}' && squeezr init");
        steps[1].ShouldBe($"cd '{RootPath}' && squeezr index");

        var wire = Wire(result);
        wire.ShouldNotContain("install");
        wire.ShouldNotContain("registration");
    }

    [Fact]
    public void From_NotInitializedSessionOutsideRoot_StepsRebindTheSessionAndNeverSuggestInit()
    {
        var result = From(
            "Workspace is not initialized at the root.", WorkspaceException.NotInitializedCode,
            WorkspaceOutsideRoot());

        var steps = Payload(result).NextSteps!.ToList();
        steps.ShouldContain(step => step.StartsWith(
            $"cd '{OutsideSession}' && squeezr mcp install", StringComparison.Ordinal));
        steps.ShouldContain(step => step.Contains("squeezr serve", StringComparison.Ordinal));
        Payload(result).Cause.ShouldBe(ToolErrorCause.SessionOutsideRoot);

        Wire(result).ShouldNotContain("squeezr init");
    }

    [Fact]
    public void From_NotInitialized_CauseIsComputedFromGeometryNotFromMessageText()
    {
        Payload(From(NotInitializedMessage, WorkspaceException.NotInitializedCode))
            .Cause.ShouldBe(ToolErrorCause.RootWithoutManifest);

        Payload(From(NotInitializedMessage, WorkspaceException.NotInitializedCode,
                new McpWorkspace(RootPath, InsideSession, null)))
            .Cause.ShouldBe(ToolErrorCause.RootWithoutManifest);

        Payload(From(NotInitializedMessage, WorkspaceException.NotInitializedCode, WorkspaceOutsideRoot()))
            .Cause.ShouldBe(ToolErrorCause.SessionOutsideRoot);
    }

    [Fact]
    public void From_RootPathNamesTheResolvedRootEvenWhenTheSessionIsElsewhere()
    {
        var payload = Payload(From(
            "Workspace is not initialized at the root.", WorkspaceException.NotInitializedCode,
            WorkspaceOutsideRoot()));

        payload.RootPath.ShouldBe(RootPath);
    }

    [Fact]
    public void EnvelopeOfWorkspaceThrownAtRoot_PrescribesInitAndIndex()
    {
        var root = TestDirs.NewTempDir();
        var result = EnvelopeForUninitialized(new McpWorkspace(root, root, null));

        var steps = Payload(result).NextSteps!.ToList();
        steps[0].ShouldBe($"cd '{root}' && squeezr init");
        steps[1].ShouldBe($"cd '{root}' && squeezr index");
        Payload(result).RootPath.ShouldBe(Path.GetFullPath(root));

        Wire(result).ShouldContain($"\"cause\":\"{ToolErrorCause.RootWithoutManifest}\"");
        Wire(result).ShouldNotContain("install");
    }

    [Fact]
    public void EnvelopeOfWorkspaceThrownAtRootWithApostrophe_StepsStayExecutableInPosixSh()
    {
        var baseDir = TestDirs.NewTempDir();
        var root = Path.Combine(baseDir, "it's-a-root");
        var quotedRoot = Path.Combine(baseDir, @"it'\''s-a-root");

        var result = EnvelopeForUninitialized(new McpWorkspace(root, root, null));

        var steps = Payload(result).NextSteps!.ToList();
        steps[0].ShouldBe($"cd '{quotedRoot}' && squeezr init");
        steps[1].ShouldBe($"cd '{quotedRoot}' && squeezr index");
    }

    [Fact]
    public void EnvelopeOfWorkspaceThrownOutsideRootWithApostrophe_RebindStepsQuoteTheSessionDir()
    {
        var baseDir = TestDirs.NewTempDir();
        var root = Path.Combine(baseDir, "elsewhere");
        var session = Path.Combine(baseDir, "it's-a-session");
        var quotedSession = Path.Combine(baseDir, @"it'\''s-a-session");

        var steps = Payload(EnvelopeForUninitialized(new McpWorkspace(root, session, null))).NextSteps!.ToList();

        steps.ShouldContain($"cd '{quotedSession}' && squeezr serve");
        steps.ShouldContain($"cd '{quotedSession}' && squeezr mcp install --client <client> --scope project");
    }

    [Fact]
    public void EnvelopeOfWorkspaceThrownOutsideRoot_PrescribesRebindAndCarriesNoInitCommand()
    {
        var result = EnvelopeForUninitialized(
            new McpWorkspace(TestDirs.NewTempDir(), TestDirs.NewTempDir(), null));

        var wire = Wire(result);
        wire.ShouldContain($"\"cause\":\"{ToolErrorCause.SessionOutsideRoot}\"");
        wire.ShouldContain("squeezr mcp install");
        wire.ShouldContain("squeezr serve");
        wire.ShouldNotContain("squeezr init");

        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text
            .ShouldNotContain("squeezr init");
    }

    [Fact]
    public void From_IndexNotBuiltWithoutConfig_AnchorsIndexStepAndGainsNoCause()
    {
        var payload = Payload(From(IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode));

        payload.NextSteps.ShouldBe([$"cd '{RootPath}' && squeezr index"]);
        payload.Cause.ShouldBeNull();
    }

    [Fact]
    public void From_IndexNotBuiltWithOllama_SuggestsServicesBeforeTheAnchoredIndexStep()
    {
        var payload = Payload(From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot("ollama")));

        payload.NextSteps.ShouldBe(["squeezr services up", $"cd '{RootPath}' && squeezr index"]);
        payload.Cause.ShouldBe(ToolErrorCause.ProviderNeedsServices);
    }

    [Fact]
    public void From_IndexNotBuiltWithAuto_ServicesComeFirstBecauseAutoPrefersTheManagedEndpoint()
    {
        var payload = Payload(From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot("auto")));

        payload.NextSteps.ShouldBe(["squeezr services up", $"cd '{RootPath}' && squeezr index"]);
        payload.Cause.ShouldBe(ToolErrorCause.ProviderNeedsServices);
    }

    [Theory]
    [InlineData("onnx")]
    [InlineData("openai")]
    public void From_IndexNotBuiltWithProviderThatNeedsNoService_KeepsOnlyTheIndexStep(string provider)
    {
        var payload = Payload(From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot(provider)));

        payload.NextSteps.ShouldBe([$"cd '{RootPath}' && squeezr index"]);
        payload.Cause.ShouldBeNull();
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("auto")]
    [InlineData("onnx")]
    [InlineData("openai")]
    [InlineData(null)]
    public void From_IndexNotBuilt_AlwaysCarriesTheIndexStep(string? provider)
    {
        var steps = Payload(From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot(provider)))
            .NextSteps!.ToList();

        steps.ShouldContain(step => step == $"cd '{RootPath}' && squeezr index");
        steps.ShouldAllBe(step => step == "squeezr services up" ||
            step.StartsWith($"cd '{RootPath}' && ", StringComparison.Ordinal));
    }

    [Fact]
    public void From_IndexNotBuilt_NeverSuggestsInitializingAStoreThatAlreadyExists()
    {
        foreach (var provider in new string?[] { "ollama", "onnx", null })
        {
            Wire(From(IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot(provider)))
                .ShouldNotContain("squeezr init");
        }
    }

    [Fact]
    public void From_IndexNotBuilt_SessionOutsideRoot_AnchorsTheRootAndNotTheSession()
    {
        var result = From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceOutsideRoot("ollama"));

        var steps = Payload(result).NextSteps!.ToList();
        steps.ShouldContain($"cd '{RootPath}' && squeezr index");
        steps.ShouldNotContain(step => step.Contains(OutsideSession, StringComparison.Ordinal));
        Payload(result).RootPath.ShouldBe(RootPath);
    }

    [Fact]
    public void From_IndexNotBuilt_RootWithApostrophe_IndexStepStaysExecutableInPosixSh()
    {
        var root = "/work/it's-root";

        var steps = Payload(From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode,
            new McpWorkspace(root, root, "ollama"))).NextSteps!.ToList();

        steps.ShouldContain(@"cd '/work/it'\''s-root' && squeezr index");
    }

    [Fact]
    public void From_IndexNotBuilt_AnswersFromTheCapturedFactNotFromTheConfigOnDisk()
    {
        var root = TestDirs.NewTempDir();
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig { Provider = "onnx" }
        });
        var workspace = new McpWorkspace(root, root, "ollama");

        File.Delete(SqueezrPaths.ConfigPath(root));
        var payload = Payload(From(IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, workspace));

        payload.NextSteps!.ShouldContain("squeezr services up");
        payload.Cause.ShouldBe(ToolErrorCause.ProviderNeedsServices);
    }

    [Fact]
    public void From_IndexNotBuilt_ServiceAdvice_KeepsFiveWireFieldsAndTheVerbatimTextBlock()
    {
        var result = From(
            IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode, WorkspaceAtRoot("ollama"));

        var json = Wire(result);
        json.ShouldContain("\"nextSteps\":[\"squeezr services up\",");
        json.ShouldContain($"\"cause\":\"{ToolErrorCause.ProviderNeedsServices}\"");
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text
            .ShouldBe(IndexNotBuiltMessage);
    }

    [Theory]
    [InlineData(WorkspaceException.InvalidQueryCode)]
    [InlineData(WorkspaceException.RegistryBusyCode)]
    [InlineData(WorkspaceException.DefaultCode)]
    public void From_CodeWithoutRemedy_GainsCodeOnly(string code)
    {
        var payload = Payload(From("No indexed files under prefix 'src'.", code));

        payload.Code.ShouldBe(code);
        payload.NextSteps.ShouldBeNull();
        payload.RootPath.ShouldBeNull();
        payload.Cause.ShouldBeNull();
    }

    [Fact]
    public void From_InternalError_GainsCodeAndKeepsInternalPrefix()
    {
        var result = ToolErrors.From(new InvalidOperationException("kaboom"), WorkspaceAtRoot());

        var payload = Payload(result);
        payload.Code.ShouldBe("internal_error");
        payload.Message.ShouldBe("Internal error: kaboom");
        payload.NextSteps.ShouldBeNull();
        result.Content.ShouldHaveSingleItem()
            .ShouldBeOfType<TextContentBlock>().Text.ShouldBe("Internal error: kaboom");
    }

    [Fact]
    public void From_UsageError_SpeaksForItselfWithoutInternalPrefix()
    {
        var result = ToolErrors.From(
            new McpUsageException("no symbol given — symbol_lookup accepts 'name' or its alias 'symbol'"),
            WorkspaceAtRoot());

        var payload = Payload(result);
        payload.Code.ShouldBe("invalid_usage");
        payload.Message.ShouldBe("no symbol given — symbol_lookup accepts 'name' or its alias 'symbol'");
        payload.Message.ShouldNotStartWith("Internal error:");
    }

    [Fact]
    public void StructuredBlock_NotInitialized_RendersWireContractNames()
    {
        var json = Wire(From(NotInitializedMessage, WorkspaceException.NotInitializedCode));

        json.ShouldContain("\"code\":\"workspace_not_initialized\"");
        json.ShouldContain("\"message\":");
        json.ShouldContain($"\"rootPath\":\"{RootPath}\"");
        json.ShouldContain("\"nextSteps\":[");
        json.ShouldContain($"\"cause\":\"{ToolErrorCause.RootWithoutManifest}\"");
    }

    [Fact]
    public void StructuredBlock_PayloadWithoutRemedy_OmitsAdviceFields()
    {
        var json = Wire(From("Bad query.", WorkspaceException.InvalidQueryCode));

        json.ShouldNotContain("rootPath");
        json.ShouldNotContain("nextSteps");
        json.ShouldNotContain("cause");
    }

    private static CallToolResult EnvelopeForUninitialized(McpWorkspace workspace)
    {
        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);
        return ToolErrors.From(ex, workspace);
    }

    private static McpWorkspace WorkspaceAtRoot(string? provider = null) => new(RootPath, RootPath, provider);

    private static McpWorkspace WorkspaceOutsideRoot(string? provider = null) =>
        new(RootPath, OutsideSession, provider);

    private static CallToolResult From(string message, string code, McpWorkspace? workspace = null) =>
        ToolErrors.From(new WorkspaceException(message, code), workspace ?? WorkspaceAtRoot());

    private static string Wire(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().ToString();

    private static ToolErrorPayload Payload(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().Deserialize<ToolErrorPayload>()!;
}
