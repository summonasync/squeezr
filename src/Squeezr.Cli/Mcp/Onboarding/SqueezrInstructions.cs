namespace Squeezr.Cli.Mcp.Onboarding;

public static class SqueezrInstructions
{
    public const string StartMarker = "<!-- squeezr:instructions -->";

    public const string EndMarker = "<!-- /squeezr:instructions -->";

    public const string Block = StartMarker + "\n" + Body + "\n" + EndMarker;

    private const string Body = """
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

        Cross-repo work: sibling repos join the same workspace with `squeezr add <path> --name <repo>`.
        If a repo you need is missing from index_status, say so instead of silently grepping.

        Output is token-budgeted: respect maxTokens and narrow the query when a result is truncated.
        A staleFiles list means the index is behind the disk - suggest `squeezr index`
        (freshness is normally maintained by the squeezr watch daemon).
        """;
}
