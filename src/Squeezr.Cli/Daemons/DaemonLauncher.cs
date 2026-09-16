using System.Diagnostics;
using System.Reflection;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Daemons;

public sealed class DaemonLauncher
{
    private static readonly string[] MuxerFileNames = ["dotnet", "dotnet.exe"];

    private readonly Func<string> _processPath;

    public DaemonLauncher() : this(ResolveProcessPath)
    {
    }

    internal DaemonLauncher(Func<string> processPath) => _processPath = processPath;

    public Process StartWatch(string rootPath)
    {
        var binaryPath = _processPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            WorkingDirectory = rootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        if (IsMuxerd(binaryPath))
        {
            startInfo.ArgumentList.Add(EntryAssemblyPath());
        }

        startInfo.ArgumentList.Add("watch");
        var process = Process.Start(startInfo)
            ?? throw new WorkspaceException("Failed to start the watch daemon process.");
        process.StandardInput.Close();
        return process;
    }

    private static bool IsMuxerd(string binaryPath) =>
        MuxerFileNames.Contains(Path.GetFileName(binaryPath), StringComparer.OrdinalIgnoreCase);

    private static string EntryAssemblyPath() =>
        Assembly.GetEntryAssembly()?.Location is { Length: > 0 } location
            ? location
            : throw new WorkspaceException("Cannot resolve the path of the current squeezr assembly.");

    private static string ResolveProcessPath() =>
        Environment.ProcessPath
        ?? throw new WorkspaceException("Cannot resolve the path of the current squeezr executable.");
}
