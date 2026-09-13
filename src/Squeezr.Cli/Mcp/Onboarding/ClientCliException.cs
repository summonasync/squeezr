namespace Squeezr.Cli.Mcp.Onboarding;

public sealed class ClientCliException : InvalidOperationException
{
    public ClientCliException(string binaryName, int exitCode, string stderr)
        : base($"Client CLI '{binaryName}' exited with code {exitCode}: {stderr}")
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    public int ExitCode { get; }

    public string Stderr { get; }
}
