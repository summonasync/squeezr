namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpClientDescriptor
{
    public required McpClientId Id { get; init; }

    public required string DisplayName { get; init; }

    public required string BinaryName { get; init; }

    public required McpConfigFormat ConfigFormat { get; init; }

    public required McpWriteStrategy WriteStrategy { get; init; }

    public bool SupportsCliScope { get; init; } = true;

    public bool UsesArgumentSeparator { get; init; } = true;

    public bool ExpectsOptionsBeforeName { get; init; }

    public bool UsesTrustFlag { get; init; }

    public string? HomeEnvironmentVariable { get; init; }

    public string? RequiredVersionNegotiation { get; init; }

    public int? StartupTimeoutSeconds { get; init; }

    public required Func<string, string, McpScope, string> ConfigPathResolver { get; init; }

    public required Func<string, string, McpScope, string> InstructionPathResolver { get; init; }

    public Func<string, string, McpScope, string>? HooksPathResolver { get; init; }

    public string GetConfigPath(string homeDir, string workspaceRoot, McpScope scope) =>
        ConfigPathResolver(homeDir, workspaceRoot, scope);

    public string GetInstructionPath(string homeDir, string workspaceRoot, McpScope scope) =>
        InstructionPathResolver(homeDir, workspaceRoot, scope);

    public string? GetHooksPath(string homeDir, string workspaceRoot, McpScope scope) =>
        HooksPathResolver?.Invoke(homeDir, workspaceRoot, scope);
}
