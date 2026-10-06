using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class ToolErrorsTests
{
    private const string RootPath = "/work/demo";
    private const string NotInitializedMessage = "Workspace is not initialized. Run 'squeezr init' first.";
    private const string IndexNotBuiltMessage = "Index is not built. Run 'squeezr index' first.";

    [Fact]
    public void From_NotInitialized_CarriesCodeRootPathAndNonEmptyNextSteps()
    {
        var result = From(NotInitializedMessage, WorkspaceException.NotInitializedCode);

        var payload = Payload(result);
        payload.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        payload.RootPath.ShouldBe(RootPath);
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
        payload.NextSteps!.ShouldAllBe(step => step.StartsWith("squeezr ", StringComparison.Ordinal));
    }

    [Fact]
    public void From_IndexNotBuilt_NextStepsLeadToIndex()
    {
        var result = From(IndexNotBuiltMessage, WorkspaceException.IndexNotBuiltCode);

        var nextSteps = Payload(result).NextSteps;
        nextSteps.ShouldNotBeNull();
        nextSteps!.ShouldContain("squeezr index");
    }

    [Theory]
    [InlineData(WorkspaceException.InvalidQueryCode)]
    [InlineData(WorkspaceException.RegistryBusyCode)]
    [InlineData(WorkspaceException.DefaultCode)]
    public void From_CodeWithoutRemedy_GainsCodeOnly(string code)
    {
        var payload = Payload(From("No indexed files under prefix 'src/'.", code));

        payload.Code.ShouldBe(code);
        payload.NextSteps.ShouldBeNull();
        payload.RootPath.ShouldBeNull();
    }

    [Fact]
    public void From_InternalError_GainsCodeAndKeepsInternalPrefix()
    {
        var result = ToolErrors.From(new InvalidOperationException("kaboom"), RootPath);

        var payload = Payload(result);
        payload.Code.ShouldBe("internal_error");
        payload.Message.ShouldBe("Internal error: kaboom");
        payload.NextSteps.ShouldBeNull();
        result.Content.ShouldHaveSingleItem()
            .ShouldBeOfType<TextContentBlock>().Text.ShouldBe("Internal error: kaboom");
    }

    [Fact]
    public void From_WithoutKnownRoot_OmitsRootPathAndKeepsNextSteps()
    {
        var payload = Payload(ToolErrors.From(
            new WorkspaceException(NotInitializedMessage, WorkspaceException.NotInitializedCode)));

        payload.RootPath.ShouldBeNull();
        payload.NextSteps.ShouldNotBeEmpty();
    }

    [Fact]
    public void StructuredBlock_NotInitialized_RendersWireContractNames()
    {
        var json = Wire(From(NotInitializedMessage, WorkspaceException.NotInitializedCode));

        json.ShouldContain("\"code\":\"workspace_not_initialized\"");
        json.ShouldContain("\"message\":");
        json.ShouldContain($"\"rootPath\":\"{RootPath}\"");
        json.ShouldContain("\"nextSteps\":[");
    }

    [Fact]
    public void StructuredBlock_PayloadWithoutRemedy_OmitsAdviceFields()
    {
        var json = Wire(From("Bad query.", WorkspaceException.InvalidQueryCode));

        json.ShouldNotContain("rootPath");
        json.ShouldNotContain("nextSteps");
    }

    private static CallToolResult From(string message, string code) =>
        ToolErrors.From(new WorkspaceException(message, code), RootPath);

    private static string Wire(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().ToString();

    private static ToolErrorPayload Payload(CallToolResult result) =>
        result.StructuredContent.ShouldNotBeNull().Deserialize<ToolErrorPayload>()!;
}
