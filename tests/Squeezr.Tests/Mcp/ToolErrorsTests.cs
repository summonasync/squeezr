using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;
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
                new McpWorkspace(RootPath, InsideSession)))
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
        var result = EnvelopeForUninitialized(new McpWorkspace(root, root));

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

        var result = EnvelopeForUninitialized(new McpWorkspace(root, root));

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

        var steps = Payload(EnvelopeForUninitialized(new McpWorkspace(root, session))).NextSteps!.ToList();

        steps.ShouldContain($"cd '{quotedSession}' && squeezr serve");
        steps.ShouldContain($"cd '{quotedSession}' && squeezr mcp install --client <client> --scope project");
    }

    [Fact]
    public void EnvelopeOfWorkspaceThrownOutsideRoot_PrescribesRebindAndCarriesNoInitCommand()
    {
        var result = EnvelopeForUninitialized(
            new McpWorkspace(TestDirs.NewTempDir(), TestDirs.NewTempDir()));

        var wire = Wire(result);
        wire.ShouldContain($"\"cause\":\"{ToolErrorCause.SessionOutsideRoot}\"");
        wire.ShouldContain("squeezr mcp install");
        wire.ShouldContain("squeezr serve");
        wire.ShouldNotContain("squeezr init");

        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text
            .ShouldNotContain("squeezr init");
    }

    [Fact]
    public void From_IndexNotBuilt_KeepsTheIndexStepAndGainsNoCause()
    {
        var payload = Payload(From(IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode));

        payload.NextSteps.ShouldBe(["squeezr index"]);
        payload.Cause.ShouldBeNull();
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

    private static McpWorkspace WorkspaceAtRoot() => new(RootPath, RootPath);

    private static McpWorkspace WorkspaceOutsideRoot() => new(RootPath, OutsideSession);

    private static CallToolResult From(string message, string code, McpWorkspace? workspace = null) =>
        ToolErrors.From(new WorkspaceException(message, code), workspace ?? WorkspaceAtRoot());

    private static string Wire(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().ToString();

    private static ToolErrorPayload Payload(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().Deserialize<ToolErrorPayload>()!;
}
