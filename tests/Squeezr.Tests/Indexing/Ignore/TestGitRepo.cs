using System.Diagnostics;

namespace Squeezr.Tests.Indexing.Ignore;

internal static class TestGitRepo
{
    public static string Init(string root, string? separateGitDir = null)
    {
        if (separateGitDir is null)
        {
            Run(root, "init");
        }
        else
        {
            Run(root, "init", "--separate-git-dir", separateGitDir);
        }

        Run(root, "config", "user.email", "lane@example.test");
        Run(root, "config", "user.name", "lane");
        return root;
    }

    public static void Add(string root, params string[] paths)
    {
        foreach (var path in paths)
        {
            Run(root, "add", "-f", "--", path);
        }
    }

    public static void Commit(string root, string message)
    {
        Run(root, "commit", "-m", message);
    }

    public static void AddWorktree(string mainRoot, string worktreePath, string branch)
    {
        Run(mainRoot, "worktree", "add", worktreePath, "-b", branch);
    }

    public static void Detach(string root)
    {
        Run(root, "checkout", "--detach");
    }

    public static void SymbolicRef(string root, string target)
    {
        Run(root, "symbolic-ref", "HEAD", target);
    }

    private static void Run(string root, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)
                         ?? throw new InvalidOperationException($"git {string.Join(' ', args)} did not start in {root}");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"git {string.Join(' ', args)} timed out in {root}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in {root}: {process.StandardError.ReadToEnd().Trim()}");
        }
    }
}
