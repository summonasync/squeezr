namespace Squeezr.Cli.Mcp.Onboarding;

public static class SqueezrInstructions
{
    public const string StartMarker = "<!-- squeezr:instructions -->";

    public const string EndMarker = "<!-- /squeezr:instructions -->";

    public const string Block = StartMarker + "\n" + Body + "\n" + EndMarker;

    private const string Body = """
        ## Squeezr - Code Navigation First

        This workspace is indexed by squeezr: a local MCP code index with 6 tools -
        repo_map, code_search, symbol_lookup, find_references, expand_span, index_status.

        Navigation flow (follow this order):
        1. repo_map - map the workspace first: projects, folders, key symbols.
        2. code_search or symbol_lookup - locate the exact file or symbol you need.
        3. find_references - trace callers and usages before changing anything.
        4. expand_span - read only the code spans you need, never whole files by default.

        Rules:
        - Do not read whole files until you have exhausted the index.
        - Output is token-budgeted: respect maxTokens on every call.
        - If a result is marked truncated, narrow the query instead of reading the full file.
        - If a result lists staleFiles, the index is behind the disk - suggest running `squeezr index`.
          Freshness is normally maintained by the squeezr watch daemon.
        """;
}
