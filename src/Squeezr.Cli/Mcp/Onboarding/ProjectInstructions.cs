namespace Squeezr.Cli.Mcp.Onboarding;

public static class ProjectInstructions
{
    public const string StartMarker = "<!-- squeezr:project-instructions -->";

    public const string EndMarker = "<!-- /squeezr:project-instructions -->";

    public static string Block => StartMarker + "\n" + Body + "\n" + EndMarker;

    private static readonly string Body = $"""
        ## Squeezr - Project Code Navigation
        Managed by squeezr v{AppVersion.Current} - do not edit inside the markers.

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
        """;
}
