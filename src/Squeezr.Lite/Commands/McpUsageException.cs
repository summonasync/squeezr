namespace Squeezr.Lite.Commands;

public sealed class McpUsageException : InvalidOperationException
{
    public McpUsageException(string message)
        : base(message)
    {
    }
}
