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

    private const string PolicyMissingFallbackNote =
        "note: Docker Compose before v2.24 does not support 'pull --policy missing'; all images will be pulled, " +
        "so re-running 'squeezr services up' without network access may fail.";

    private static readonly Version PolicyMissingMinVersion = new(2, 24);
    private static readonly string[] PullArguments = ["pull"];
    private static readonly string[] PolicyMissingPullArguments = ["pull", "--policy", "missing"];

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
        CancellationToken cancellationToken,
        Action<string>? progressHandler = null)
    {
        var argv = installation.Plugin
            ? new List<string> { "compose", "-f", composeFile, "-p", ServicesComposeFile.ProjectName }
            : new List<string> { "-f", composeFile, "-p", ServicesComposeFile.ProjectName };
        argv.AddRange(arguments);
        return _runner.RunAsync(
            new ProcessInvocation(installation.Executable, argv, timeout, progressHandler), cancellationToken);
    }

    public async Task<ProcessRunResult> RunCheckedAsync(
        DockerComposeInstallation installation,
        string composeFile,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? progressHandler = null)
    {
        ProcessRunResult result;
        try
        {
            result = await RunAsync(installation, composeFile, arguments, timeout, cancellationToken, progressHandler);
        }
        catch (TimeoutException ex)
        {
            throw new ServicesException(
                $"'{DisplayCommand(installation, arguments)}' timed out after {timeout}.",
                ServicesException.ComposeCommandFailedCode,
                ex);
        }

        if (result.ExitCode != 0)
        {
            throw ComposeFailed(installation, arguments, result);
        }

        if (result.StdErr.Length > 0)
        {
            Console.Error.Write(result.StdErr);
        }

        return result;
    }

    public async Task PullAsync(
        DockerComposeInstallation installation,
        string composeFile,
        Action<string>? progressHandler,
        CancellationToken cancellationToken)
    {
        var supportsPolicyMissing = SupportsPolicyMissing(installation);
        var arguments = supportsPolicyMissing ? PolicyMissingPullArguments : PullArguments;
        if (!supportsPolicyMissing)
        {
            Console.Error.WriteLine(PolicyMissingFallbackNote);
        }

        var result = await RunAsync(
            installation, composeFile, arguments, Timeout.InfiniteTimeSpan, cancellationToken, progressHandler);

        if (result.ExitCode != 0)
        {
            throw new ServicesException(
                $"'{DisplayCommand(installation, arguments)}' failed with exit code {result.ExitCode}: {FailureDetail(result)}. " +
                PullResumeHint(installation, composeFile),
                ServicesException.ComposeCommandFailedCode);
        }
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

    private static string DisplayCommand(DockerComposeInstallation installation, IReadOnlyList<string> arguments) =>
        installation.Plugin
            ? $"docker compose {string.Join(" ", arguments)}"
            : $"docker-compose {string.Join(" ", arguments)}";

    private static string ManualPullCommand(DockerComposeInstallation installation, string composeFile) =>
        $"{(installation.Plugin ? "docker compose" : "docker-compose")} -f {composeFile} -p {ServicesComposeFile.ProjectName} pull";

    private static bool SupportsPolicyMissing(DockerComposeInstallation installation) =>
        installation.Version is not null && installation.Version >= PolicyMissingMinVersion;

    private static string PullResumeHint(DockerComposeInstallation installation, string composeFile) =>
        $"Resume the image download manually with: {ManualPullCommand(installation, composeFile)} " +
        "— once it completes, re-run 'squeezr services up'.";

    private static ServicesException ComposeFailed(
        DockerComposeInstallation installation,
        IReadOnlyList<string> arguments,
        ProcessRunResult result) =>
        new(
            $"'{DisplayCommand(installation, arguments)}' failed with exit code {result.ExitCode}: {FailureDetail(result)}",
            ServicesException.ComposeCommandFailedCode);

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
