using System.Diagnostics;

namespace Squeezr.Cli.Mcp.Onboarding;

public sealed class ClientCliBridge
{
    public const string ServerName = "squeezr";

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ClientCliLocator _locator;
    private readonly TimeSpan _commandTimeout;

    public ClientCliBridge(ClientCliLocator locator, TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public async Task AddAsync(McpClientDescriptor client, McpScope scope, string commandPath, bool watch, CancellationToken cancellationToken)
    {
        var binary = RequireBinary(client);
        await RunAsync(binary, BuildAddArguments(client, scope, commandPath, watch), cancellationToken);
    }

    public Task RemoveAsync(McpClientDescriptor client, CancellationToken cancellationToken)
    {
        var binary = RequireBinary(client);
        return RunAsync(binary, ["mcp", "remove", ServerName], cancellationToken);
    }

    public Task<string> ListAsync(McpClientDescriptor client, CancellationToken cancellationToken)
    {
        var binary = RequireBinary(client);
        return RunAsync(binary, ["mcp", "list"], cancellationToken);
    }

    private string RequireBinary(McpClientDescriptor client) =>
        _locator.Locate(client.BinaryName)
        ?? throw new FileNotFoundException($"Client CLI '{client.BinaryName}' was not found in PATH.", client.BinaryName);

    private static IReadOnlyList<string> BuildAddArguments(McpClientDescriptor client, McpScope scope, string commandPath, bool watch)
    {
        List<string> arguments = ["mcp", "add", ServerName];
        if (client.SupportsCliScope)
        {
            arguments.Add("-s");
            arguments.Add(scope.ToString().ToLowerInvariant());
        }
        arguments.Add("--");
        arguments.Add(commandPath);
        arguments.Add("serve");
        if (watch)
        {
            arguments.Add("--watch");
        }
        return arguments;
    }

    private async Task<string> RunAsync(string binary, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = binary;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var stdoutReader = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrReader = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillSilently(process);
            await ObserveReadersAsync(stdoutReader, stderrReader);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Client CLI '{Path.GetFileName(binary)}' did not exit within {_commandTimeout}.");
        }
        var stdout = await stdoutReader;
        var stderr = await stderrReader;
        if (process.ExitCode != 0)
        {
            throw new ClientCliException(Path.GetFileName(binary), process.ExitCode, stderr.Trim());
        }
        return stdout;
    }

    private static void KillSilently(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return;
        }
    }

    private static async Task ObserveReadersAsync(Task stdoutReader, Task stderrReader)
    {
        try
        {
            await Task.WhenAll(stdoutReader, stderrReader);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return;
        }
    }
}
