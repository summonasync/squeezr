namespace Squeezr.Cli.Mcp.Onboarding;

// Where the SessionStart block landed and the exact JSON that was written there. The install door prints this
// rather than a bare verb, because adopting a hook into the user's own client config is the one act that has to
// be readable before it is trusted — and the block is the thing the README snippet promises to match.
public sealed record SessionStartHookWrite(string Path, string Block);
