using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Squeezr.Core.Indexing.Ignore;

public sealed class GitTrackedSet
{
    private const int ReadTimeoutMs = 30_000;
    private const int DrainJoinMs = 5_000;

    private readonly FrozenSet<string> _files;
    private readonly FrozenSet<string> _directories;

    private GitTrackedSet(FrozenSet<string> files, FrozenSet<string> directories)
    {
        _files = files;
        _directories = directories;
    }

    public static GitTrackedSet? Load(string repoRoot, Action<string>? warn = null)
    {
        if (!HasRepoMarker(repoRoot))
            return null;

        if (!TryReadTrackedPaths(repoRoot, warn, out var output))
            return null;

        var comparer = PathComparer;
        var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var directories = new HashSet<string>(comparer);
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            while (slash > 0)
            {
                directories.Add(file[..slash]);
                slash = file.LastIndexOf('/', slash - 1);
            }
        }

        return new GitTrackedSet(files.ToFrozenSet(comparer), directories.ToFrozenSet(comparer));
    }

    public bool ContainsFile(string relativePath) => _files.Contains(relativePath);

    public bool ContainsDirectory(string relativePath) => _directories.Contains(relativePath);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool HasRepoMarker(string repoRoot) =>
        Directory.Exists(Path.Combine(repoRoot, ".git")) || File.Exists(Path.Combine(repoRoot, ".git"));

    private static bool TryReadTrackedPaths(string repoRoot, Action<string>? warn, out string output)
    {
        output = string.Empty;
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        start.ArgumentList.Add("--cached");

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            warn?.Invoke($"git ls-files could not start in '{repoRoot}': {ex.Message}; gitignore decides without the tracked-set guard");
            return false;
        }

        if (process is null)
        {
            warn?.Invoke($"git ls-files could not start in '{repoRoot}'; gitignore decides without the tracked-set guard");
            return false;
        }

        using (process)
        {
            using var stdout = new MemoryStream();
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var readError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ReadTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                warn?.Invoke($"git ls-files did not finish within {ReadTimeoutMs / 1000}s in '{repoRoot}'; gitignore decides without the tracked-set guard");
                return false;
            }

            if (!Task.WaitAll([readOutput, readError], DrainJoinMs))
            {
                warn?.Invoke($"git ls-files output in '{repoRoot}' never drained; gitignore decides without the tracked-set guard");
                return false;
            }

            if (process.ExitCode != 0)
            {
                warn?.Invoke($"git ls-files failed in '{repoRoot}': {readError.Result.Trim()} ; gitignore decides without the tracked-set guard");
                return false;
            }

            output = Encoding.UTF8.GetString(stdout.ToArray());
            return true;
        }
    }
}
