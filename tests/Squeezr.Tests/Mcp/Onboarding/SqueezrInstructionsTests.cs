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
    public void Block_DescribesScenarioMatrixInOrder()
    {
        var map = SqueezrInstructions.Block.IndexOf("repo_map first", StringComparison.Ordinal);
        var lookup = SqueezrInstructions.Block.IndexOf("returns the method", StringComparison.Ordinal);
        var references = SqueezrInstructions.Block.IndexOf("find_references first", StringComparison.Ordinal);
        var search = SqueezrInstructions.Block.IndexOf("hybrid", StringComparison.Ordinal);
        var expand = SqueezrInstructions.Block.IndexOf("live file", StringComparison.Ordinal);

        map.ShouldBeGreaterThan(0);
        map.ShouldBeLessThan(lookup);
        lookup.ShouldBeLessThan(references);
        references.ShouldBeLessThan(search);
        search.ShouldBeLessThan(expand);
    }

    [Fact]
    public void Block_MatchesPinnedContent()
    {
        const string pinned = """
            <!-- squeezr:instructions -->
            ## Squeezr - Code Navigation

            This workspace is indexed by squeezr: a local MCP code index with 6 tools -
            repo_map, code_search, symbol_lookup, find_references, expand_span, index_status.

            Pick the tool by scenario:
            - Unfamiliar repo - repo_map first: projects, folders, key symbols.
            - Exact symbol or signature - symbol_lookup: budgeted, returns the method, not the file.
            - About to edit a symbol - find_references first: call-graph, no false positives on name matches.
            - Paraphrase or domain question - code_search: hybrid, matches exact identifiers and semantics.
            - Read a span of code - expand_span: reads the live file from disk.
            - Exact string search, or a repo not in the index - grep/read is correct, not a violation.

            Cross-repo work: sibling repos join the same workspace with `squeezr add <path>`.
            If a repo you need is missing from index_status, say so instead of silently grepping.

            Output is token-budgeted: respect maxTokens and narrow the query when a result is truncated.
            A staleFiles list means the index is behind the disk - suggest `squeezr index`
            (freshness is normally maintained by the squeezr watch daemon).
            <!-- /squeezr:instructions -->
            """;

        SqueezrInstructions.Block.ShouldBe(pinned.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Block_AllowsGrepForExactStringsAndUnindexedRepos()
    {
        SqueezrInstructions.Block.ShouldContain("grep/read is correct, not a violation");
    }

    [Fact]
    public void Block_GuidesCrossRepoWorkspaces()
    {
        SqueezrInstructions.Block.ShouldContain("squeezr add <path>");
        SqueezrInstructions.Block.ShouldContain("missing from index_status");
    }

    [Fact]
    public void Block_IsWrappedInMarkers()
    {
        SqueezrInstructions.Block.ShouldStartWith(SqueezrInstructions.StartMarker);
        SqueezrInstructions.Block.ShouldEndWith(SqueezrInstructions.EndMarker);
    }
}
