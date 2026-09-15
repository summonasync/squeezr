namespace Squeezr.Cli.Infra;

public sealed class ServicesException : Exception
{
    public const string DockerUnavailableCode = "docker_unavailable";
    public const string ComposeCommandFailedCode = "docker_compose_failed";

    public ServicesException(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
