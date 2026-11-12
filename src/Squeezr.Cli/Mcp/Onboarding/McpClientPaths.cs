namespace Squeezr.Cli.Mcp.Onboarding;

public static class McpClientPaths
{
    public static string ConfigPath(McpClientDescriptor client, McpOnboardingEnvironment environment, McpScope scope) =>
        Resolve(client.GetConfigPath(environment.HomeDir, environment.WorkspaceRoot, scope), client, scope, environment.ClientHomeOverride);

    public static string InstructionPath(McpClientDescriptor client, McpOnboardingEnvironment environment, McpScope scope) =>
        Resolve(client.GetInstructionPath(environment.HomeDir, environment.WorkspaceRoot, scope), client, scope, environment.ClientHomeOverride);

    public static string? HooksPath(McpClientDescriptor client, McpOnboardingEnvironment environment, McpScope scope) =>
        client.GetHooksPath(environment.HomeDir, environment.WorkspaceRoot, scope) is { } path
            ? Resolve(path, client, scope, environment.ClientHomeOverride)
            : null;

    private static string Resolve(string defaultPath, McpClientDescriptor client, McpScope scope, string? homeOverride) =>
        scope == McpScope.User && client.HomeEnvironmentVariable is not null && !string.IsNullOrWhiteSpace(homeOverride)
            ? Path.Combine(homeOverride, Path.GetFileName(defaultPath))
            : defaultPath;
}
