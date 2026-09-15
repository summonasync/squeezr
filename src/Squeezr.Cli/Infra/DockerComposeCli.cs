using System.ComponentModel;
using System.Text.Json;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Infra;

public sealed class DockerComposeCli
{
    public static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DownTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(10);

    private const int MaxFailureDetailLength = 500;

    private const string InstallHint =
        "Docker with the Compose plugin was not found. Squeezr services (Qdrant and Ollama) require it. " +
        "Install Docker Engine: https://docs.docker.com/engine/install/";

    private readonly IProcessRunner _runner;

    public DockerComposeCli(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<DockerComposeInstallation> DetectAsync(CancellationToken cancellationToken)
    {
        var plugin = await TryDetectAsync("docker", ["compose", "version"], cancellationToken);
        if (plugin is not null)
        {
            return plugin with { Plugin = true };
        }

        var standalone = await TryDetectAsync("docker-compose", ["version"], cancellationToken);
        if (standalone is not null)
        {
            return standalone;
        }

        throw new ServicesException(InstallHint, ServicesException.DockerUnavailableCode);
    }

    public Task<ProcessRunResult> RunAsync(
        DockerComposeInstallation installation,
        string composeFile,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var argv = installation.Plugin
            ? new List<string> { "compose", "-f", composeFile, "-p", ServicesComposeFile.ProjectName }
            : new List<string> { "-f", composeFile, "-p", ServicesComposeFile.ProjectName };
        argv.AddRange(arguments);
        return _runner.RunAsync(new ProcessInvocation(installation.Executable, argv, timeout), cancellationToken);
    }

    public async Task<ProcessRunResult> RunCheckedAsync(
        DockerComposeInstallation installation,
        string composeFile,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await RunAsync(installation, composeFile, arguments, timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            var display = installation.Plugin
                ? $"docker compose {string.Join(" ", arguments)}"
                : $"docker-compose {string.Join(" ", arguments)}";
            throw new ServicesException(
                $"'{display}' timed out after {timeout}.",
                ServicesException.ComposeCommandFailedCode,
                ex);
        }

        if (result.ExitCode != 0)
        {
            var display = installation.Plugin
                ? $"docker compose {string.Join(" ", arguments)}"
                : $"docker-compose {string.Join(" ", arguments)}";
            throw new ServicesException(
                $"'{display}' failed with exit code {result.ExitCode}: {FailureDetail(result)}",
                ServicesException.ComposeCommandFailedCode);
        }

        if (result.StdErr.Length > 0)
        {
            Console.Error.Write(result.StdErr);
        }

        return result;
    }

    public static IReadOnlyList<ComposeServiceState> ParseServiceStates(string psOutput)
    {
        if (string.IsNullOrWhiteSpace(psOutput))
        {
            return [];
        }

        using var document = ParsePsDocument(psOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw UnsupportedPsFormat();
        }

        var states = new List<ComposeServiceState>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            states.Add(new ComposeServiceState(
                PropertyOrEmpty(element, "Service"),
                PropertyOrEmpty(element, "Name"),
                PropertyOrEmpty(element, "State"),
                PropertyOrEmpty(element, "Status")));
        }

        return states;
    }

    private async Task<DockerComposeInstallation?> TryDetectAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessInvocation(executable, arguments, DetectTimeout), cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            return null;
        }

        return result.ExitCode == 0
            ? new DockerComposeInstallation(executable, Plugin: false, DockerComposeVersion.TryParse(result.StdOut))
            : null;
    }

    private static JsonDocument ParsePsDocument(string psOutput)
    {
        try
        {
            return JsonDocument.Parse(psOutput);
        }
        catch (JsonException ex)
        {
            throw UnsupportedPsFormat(ex);
        }
    }

    private static ServicesException UnsupportedPsFormat(Exception? innerException = null) =>
        new(
            "'docker compose ps --format json' returned unexpected output; " +
            "it requires Docker Compose v2.21 or newer. Upgrade Docker Engine: https://docs.docker.com/engine/install/",
            ServicesException.ComposeCommandFailedCode,
            innerException);

    private static string PropertyOrEmpty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property.GetString() ?? string.Empty : string.Empty;

    private static string FailureDetail(ProcessRunResult result)
    {
        var detail = result.StdErr.Trim();
        if (detail.Length == 0)
        {
            detail = result.StdOut.Trim();
        }

        return detail.Length > MaxFailureDetailLength ? detail[^MaxFailureDetailLength..] : detail;
    }
}
