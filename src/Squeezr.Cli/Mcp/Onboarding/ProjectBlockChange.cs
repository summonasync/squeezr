namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record ProjectBlockChange(IReadOnlyList<string> Written, IReadOnlyList<string> Orphaned);
