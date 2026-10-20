namespace Squeezr.Core.Storage;

public static class CacheEligibleTools
{
    // the response cache itself lives in the CLI layer, so Core mirrors the eligible set by tool name; rows
    // written by anything else (CLI commands, expand_span, index_status) can never produce a cache hit and
    // would only inflate the hit-rate denominator. CacheEligibleToolsGuardTests ties this list to the tools
    // that actually hold a ToolResponseCache.
    public static bool Includes(string tool) =>
        tool is "code_search" or "symbol_lookup" or "repo_map" or "find_references";
}
