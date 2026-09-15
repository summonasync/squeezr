namespace Squeezr.Cli.Infra;

public sealed record DockerComposeInstallation(string Executable, bool Plugin, Version? Version);
