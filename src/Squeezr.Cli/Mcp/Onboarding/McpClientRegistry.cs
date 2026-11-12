namespace Squeezr.Cli.Mcp.Onboarding;

public static class McpClientRegistry
{
    private static readonly McpClientDescriptor[] Clients =
    [
        new McpClientDescriptor
        {
            Id = McpClientId.Claude,
            DisplayName = "Claude Code",
            BinaryName = "claude",
            ConfigFormat = McpConfigFormat.Json,
            WriteStrategy = McpWriteStrategy.CliFirst,
            SupportsCliScope = true,
            ConfigPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".claude.json")
                : Path.Combine(root, ".mcp.json"),
            InstructionPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".claude", "CLAUDE.md")
                : Path.Combine(root, "CLAUDE.md"),
            HooksPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".claude", "settings.json")
                : Path.Combine(root, ".claude", "settings.json"),
        },
        new McpClientDescriptor
        {
            Id = McpClientId.Kimi,
            DisplayName = "Kimi Code",
            BinaryName = "kimi",
            ConfigFormat = McpConfigFormat.Json,
            WriteStrategy = McpWriteStrategy.WriterOnly,
            SupportsCliScope = false,
            HomeEnvironmentVariable = "KIMI_CODE_HOME",
            ConfigPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".kimi-code", "mcp.json")
                : Path.Combine(root, ".kimi-code", "mcp.json"),
            InstructionPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".kimi-code", "AGENTS.md")
                : Path.Combine(root, "AGENTS.md"),
        },
        new McpClientDescriptor
        {
            Id = McpClientId.Qwen,
            DisplayName = "Qwen Code",
            BinaryName = "qwen",
            ConfigFormat = McpConfigFormat.Json,
            WriteStrategy = McpWriteStrategy.CliFirst,
            RequiredVersionNegotiation = "auto",
            SupportsCliScope = true,
            UsesArgumentSeparator = false,
            ExpectsOptionsBeforeName = true,
            UsesTrustFlag = true,
            ConfigPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".qwen", "settings.json")
                : Path.Combine(root, ".qwen", "settings.json"),
            InstructionPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".qwen", "QWEN.md")
                : Path.Combine(root, "QWEN.md"),
            // Qwen keeps mcpServers and hooks in the same settings file — the hooks block is an additive sibling key.
            HooksPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".qwen", "settings.json")
                : Path.Combine(root, ".qwen", "settings.json"),
        },
        new McpClientDescriptor
        {
            Id = McpClientId.Codex,
            DisplayName = "Codex CLI",
            BinaryName = "codex",
            ConfigFormat = McpConfigFormat.Toml,
            WriteStrategy = McpWriteStrategy.CliFirst,
            SupportsCliScope = false,
            StartupTimeoutSeconds = 30,
            ConfigPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".codex", "config.toml")
                : Path.Combine(root, ".codex", "config.toml"),
            InstructionPathResolver = static (home, root, scope) => scope == McpScope.User
                ? Path.Combine(home, ".codex", "AGENTS.md")
                : Path.Combine(root, "AGENTS.md"),
        },
    ];

    public static IReadOnlyList<McpClientDescriptor> All => Clients;

    public static McpClientDescriptor Get(McpClientId id) => Clients.First(c => c.Id == id);

    public static bool TryGet(string name, out McpClientDescriptor descriptor)
    {
        descriptor = null!;
        var normalized = name.Trim().ToLowerInvariant()
            .Replace("-code", string.Empty, StringComparison.Ordinal)
            .Replace("-cli", string.Empty, StringComparison.Ordinal);
        for (var i = 0; i < Clients.Length; i++)
        {
            if (string.Equals(Clients[i].Id.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = Clients[i];
                return true;
            }
        }
        return false;
    }
}
