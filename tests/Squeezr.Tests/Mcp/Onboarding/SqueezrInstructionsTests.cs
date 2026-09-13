using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class SqueezrInstructionsTests
{
    [Fact]
    public void Block_ContainsAllSixToolNames()
    {
        foreach (var tool in new[] { "repo_map", "code_search", "symbol_lookup", "find_references", "expand_span", "index_status" })
        {
            SqueezrInstructions.Block.ShouldContain(tool);
        }
    }

    [Fact]
    public void Block_DescribesNavigationFlowInOrder()
    {
        var map = SqueezrInstructions.Block.IndexOf("repo_map", StringComparison.Ordinal);
        var search = SqueezrInstructions.Block.IndexOf("code_search", StringComparison.Ordinal);
        var references = SqueezrInstructions.Block.IndexOf("find_references", StringComparison.Ordinal);
        var expand = SqueezrInstructions.Block.IndexOf("expand_span", StringComparison.Ordinal);

        map.ShouldBeLessThan(search);
        search.ShouldBeLessThan(references);
        references.ShouldBeLessThan(expand);
    }

    [Fact]
    public void Block_ContainsNavigationRules()
    {
        SqueezrInstructions.Block.ShouldContain("Do not read whole files until you have exhausted the index");
        SqueezrInstructions.Block.ShouldContain("maxTokens");
        SqueezrInstructions.Block.ShouldContain("truncated");
        SqueezrInstructions.Block.ShouldContain("staleFiles");
        SqueezrInstructions.Block.ShouldContain("squeezr index");
    }

    [Fact]
    public void Block_IsWrappedInMarkers()
    {
        SqueezrInstructions.Block.ShouldStartWith(SqueezrInstructions.StartMarker);
        SqueezrInstructions.Block.ShouldEndWith(SqueezrInstructions.EndMarker);
    }
}
