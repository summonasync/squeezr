using ModelContextProtocol.Server;

namespace Squeezr.Cli.Mcp;

public sealed class ClientIdentity
{
    private volatile McpServer? _server;

    public string? Name => _server?.ClientInfo?.Name;

    public string? Version => _server?.ClientInfo?.Version;

    internal void Attach(McpServer server) => _server = server;
}
