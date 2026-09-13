namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpInstallDecision(McpInstallMethod Method, string? Warning);
