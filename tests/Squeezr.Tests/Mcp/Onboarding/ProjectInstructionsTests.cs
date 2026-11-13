using Shouldly;
using Squeezr.Cli;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class ProjectInstructionsTests
{
    [Fact]
    public void Block_MatchesPinnedContent()
    {
        var pinned = $$"""
            <!-- squeezr:project-instructions -->
            ## Squeezr - Project Code Navigation
            Managed by squeezr v{{AppVersion.Current}} - do not edit inside the markers.

            This project is indexed by squeezr: a local MCP code index with 6 tools -
            repo_map, code_search, symbol_lookup, find_references, expand_span, index_status.

            Pick the tool by scenario:
            - Unfamiliar repo - repo_map first: projects, folders, key symbols.
            - Exact symbol or signature - symbol_lookup: budgeted, returns the method, not the file.
            - About to edit a symbol - find_references first: call-graph, no false positives on name matches.
            - Paraphrase or domain question - code_search: hybrid, matches exact identifiers and semantics.
            - Read a span of code - expand_span: reads the live file from disk.
            - Exact string search, or a repo not in the index - grep/read is correct, not a violation.

            Cross-repo work: sibling repos join the same workspace with `squeezr add <path> --name <repo>`.
            If a repo you need is missing from index_status, say so instead of silently grepping.

            Commands:
            - `squeezr add <path> --name <repo>` - add a sibling repo to the workspace.
            - `squeezr index` - refresh the index now.
            - `squeezr watch` - keep the index fresh automatically.
            - `squeezr services status` - check qdrant and ollama health.
            - `squeezr stats` - show token savings and usage.

            Rules:
            - index_status reports a non-empty staleFiles list - tell the user explicitly and suggest `squeezr index` or checking the watch daemon.
            - A repo missing from index_status - say so instead of silently grepping.
            - Tool output is token-budgeted: respect maxTokens and narrow the query when a result is truncated.

            To refresh this block, re-run `squeezr mcp install --project`.
            <!-- /squeezr:project-instructions -->
            """;

        ProjectInstructions.Block.ShouldBe(pinned.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Block_HeaderCarriesCurrentAppVersion()
    {
        ProjectInstructions.Block.ShouldContain($"Managed by squeezr v{AppVersion.Current}");
    }

    [Fact]
    public void Block_DescribesScenarioMatrixInOrder()
    {
        var map = ProjectInstructions.Block.IndexOf("repo_map first", StringComparison.Ordinal);
        var lookup = ProjectInstructions.Block.IndexOf("returns the method", StringComparison.Ordinal);
        var references = ProjectInstructions.Block.IndexOf("find_references first", StringComparison.Ordinal);
        var search = ProjectInstructions.Block.IndexOf("hybrid", StringComparison.Ordinal);
        var expand = ProjectInstructions.Block.IndexOf("live file", StringComparison.Ordinal);

        map.ShouldBeGreaterThan(0);
        map.ShouldBeLessThan(lookup);
        lookup.ShouldBeLessThan(references);
        references.ShouldBeLessThan(search);
        search.ShouldBeLessThan(expand);
    }

    [Fact]
    public void Block_ContainsCommandCheatSheet()
    {
        foreach (var command in new[]
        {
            "`squeezr add <path> --name <repo>`",
            "`squeezr index`",
            "`squeezr watch`",
            "`squeezr services status`",
            "`squeezr stats`",
        })
        {
            ProjectInstructions.Block.ShouldContain(command);
        }
    }

    [Fact]
    public void Block_ContainsBehavioralRules()
    {
        ProjectInstructions.Block.ShouldContain("non-empty staleFiles list - tell the user explicitly");
        ProjectInstructions.Block.ShouldContain("checking the watch daemon");
        ProjectInstructions.Block.ShouldContain("missing from index_status - say so instead of silently grepping");
        ProjectInstructions.Block.ShouldContain("respect maxTokens");
    }

    [Fact]
    public void Block_DocumentsSelfUpdate()
    {
        ProjectInstructions.Block.ShouldContain("re-run `squeezr mcp install --project`");
    }

    [Fact]
    public void Block_IsWrappedInProjectMarkers()
    {
        ProjectInstructions.Block.ShouldStartWith(ProjectInstructions.StartMarker);
        ProjectInstructions.Block.ShouldEndWith(ProjectInstructions.EndMarker);
        ProjectInstructions.Block.ShouldContain("squeezr:project-instructions");
    }

    // The nested wording is the one place this block is allowed to name another tree, and the pin is on the
    // emitted text rather than on a resolution: what the block must not do is claim the parent as this
    // checkout's index, and what it must do is name the store that answers and the verb that mints its own.
    [Fact]
    public void BlockServedByOtherStore_DeniesTheClaimItCannotSupport_KeepsEveryGuidanceSection()
    {
        var block = ProjectInstructions.BlockServedByOtherStore("/repo/main", "main-1a2b3c4d");

        block.ShouldNotContain("This project is indexed by squeezr");
        block.ShouldContain("'/repo/main'");
        block.ShouldContain("store main-1a2b3c4d");
        block.ShouldContain("squeezr worktree init");
        block.ShouldContain("Pick the tool by scenario");
        block.ShouldContain("re-run `squeezr mcp install --project`");
        block.ShouldStartWith(ProjectInstructions.StartMarker);
        block.ShouldEndWith(ProjectInstructions.EndMarker);
    }

    [Fact]
    public void BlockServedByOtherStore_WithoutAStoreKeyToName_SaysUnknownRatherThanNothing()
    {
        ProjectInstructions.BlockServedByOtherStore("/repo/main", null)
            .ShouldContain("store unknown");
    }
}
