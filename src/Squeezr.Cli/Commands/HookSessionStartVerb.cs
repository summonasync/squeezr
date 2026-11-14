using System.Reflection;
using Squeezr.Cli.Infra;

namespace Squeezr.Cli.Commands;

// The whole job of the session-start hook: call the 'worktree init' verb and hand back what it answered.
// Registration, fill and reconcile are that verb's contract — measured by its own tasks, exit-zero on the
// states a session must not trip over — so this class holds no index logic of its own and a second
// implementation cannot drift away from the command a human runs.
//
// The child is this same build: the muxer case names the entry assembly the way DaemonLauncher does, because
// re-invoking 'dotnet' without the assembly would run nothing at all.
public static class HookSessionStartVerb
{
    private static readonly string[] MuxerFileNames = ["dotnet", "dotnet.exe"];

    // The budget a session-start pass may take before the hook stops waiting and stays silent. The documented
    // snippet raises the host's own hook timeout above this so the verb, not the host, is what ends the pass.
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public static Task<ProcessRunResult> InitAsync(string targetRoot, CancellationToken cancellationToken) =>
        new ProcessRunner().RunAsync(InvocationFor(targetRoot), cancellationToken);

    private static ProcessInvocation InvocationFor(string targetRoot)
    {
        var program = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the path of the current squeezr executable.");
        var arguments = new List<string>();
        if (IsMuxerd(program))
        {
            arguments.Add(EntryAssemblyPath());
        }

        arguments.AddRange(["worktree", "init", targetRoot, "--json"]);
        return new ProcessInvocation(program, arguments, Budget);
    }

    private static bool IsMuxerd(string program) =>
        MuxerFileNames.Contains(Path.GetFileName(program), StringComparer.OrdinalIgnoreCase);

    private static string EntryAssemblyPath() =>
        Assembly.GetEntryAssembly()?.Location is { Length: > 0 } location
            ? location
            : throw new InvalidOperationException("Cannot resolve the path of the current squeezr assembly.");
}
