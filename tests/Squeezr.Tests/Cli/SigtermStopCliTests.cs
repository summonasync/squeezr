using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Cli;

// The stop path an autostarted daemon actually travels: systemctl stop retires the unit by sending SIGTERM
// to every process left in it, so both the supervisor and the watch it manages are asked to stop this way.
// Before the registration existed both died taking the signal's default action — exit 143 measured on the
// pre-change build — with the lock, the owner claim and the beats left on disk for the staleness rules.
[Collection("ProcessGlobals")]
public sealed class SigtermStopCliTests
{
    private const int Sigterm = 15;
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(30);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [LinuxFact]
    public async Task Watchd_Sigterm_StopsThroughTheConsolePath_ExitsZeroAndReleasesTheLock()
    {
        var home = TestDirs.NewTempDir();
        var lockPath = WatchdPaths.LockPathUnder(home);
        using var child = SignalledChild.Start(home, TestDirs.NewTempDir(), "watchd");
        await child.WaitForPathAsync(
            () => File.Exists(lockPath) ? lockPath : null,
            $"watchd never took {lockPath}");

        child.SendSigterm();
        var stopped = await child.ExitedWithinAsync(StopBudget);

        stopped.ShouldBeTrue($"watchd was still running {StopBudget.TotalSeconds:F0}s after SIGTERM; {child.State()}");
        child.ExitCode.ShouldBe(0, $"SIGTERM took the default action instead of the stop path's 0; {child.State()}");
        File.Exists(lockPath).ShouldBeFalse("the stopped supervisor left its lock for the next one to reap");
        child.StdErr.ShouldNotContain("Unhandled exception");
    }

    // Two signals back to back is what a stop looks like from the outside: systemd repeats nothing, but an
    // operator who is impatient does, and the second one must not turn a clean stop into a crash on the
    // runtime's signal thread.
    [LinuxFact]
    public async Task Watchd_SigtermTwice_StopsOnceAndCarriesOneExit()
    {
        var home = TestDirs.NewTempDir();
        var lockPath = WatchdPaths.LockPathUnder(home);
        using var child = SignalledChild.Start(home, TestDirs.NewTempDir(), "watchd");
        await child.WaitForPathAsync(
            () => File.Exists(lockPath) ? lockPath : null,
            $"watchd never took {lockPath}");

        child.SendSigterm();
        child.SendSigterm();
        var stopped = await child.ExitedWithinAsync(StopBudget);

        stopped.ShouldBeTrue($"watchd was still running {StopBudget.TotalSeconds:F0}s after two SIGTERMs; {child.State()}");
        child.ExitCode.ShouldBe(0, $"the second SIGTERM changed the stop; {child.State()}");
        File.Exists(lockPath).ShouldBeFalse("the stopped supervisor left its lock for the next one to reap");
        child.StdErr.ShouldNotContain("Unhandled exception");
    }

    [LinuxFact]
    public async Task Watch_SigtermWhileOwningTheStore_ReleasesClaimAndDropsBeats()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var init = await CliTestProcess.RunWithHomeAsync(home, workspace, "init", "--name", "sigterm");
        init.ExitCode.ShouldBe(0, init.StdErr);
        using var child = SignalledChild.Start(home, workspace, "watch");
        // The store dir is the child's own business: it sits inside the home the child was pinned to, so the
        // claim file is looked for under that home rather than recomputed from this process's registry.
        var claimPath = await child.WaitForPathAsync(
            () => Directory
                .EnumerateFiles(home, WatchOwnerMarker.MarkerFileName, SearchOption.AllDirectories)
                .FirstOrDefault(),
            $"watch never claimed anything under {home}");
        var storeDir = Path.GetDirectoryName(claimPath)!;

        // The claim lands before the beats do, so a ready test on the claim alone can signal a process that
        // never stamped anything: the two absence asserts below would then pass without measuring a cleanup.
        // Required here: the kind heartbeat and this pid's own beat, the exact files the stop must drop.
        var kindBeatPath = DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.DefaultKind);
        var ownBeatPath = DaemonHeartbeat.BeatPathFor(storeDir, DaemonHeartbeat.DefaultKind, child.Pid);
        await child.WaitForFilesAsync(
            [kindBeatPath, ownBeatPath],
            $"watch claimed the store but never stamped {kindBeatPath} and {ownBeatPath}");
        var beatsDir = DaemonHeartbeat.BeatsDirFor(storeDir);
        var beatsBefore = Directory.GetFiles(beatsDir, "*" + DaemonHeartbeat.BeatExtension);
        beatsBefore.ShouldContain(ownBeatPath, "the beat the stop is expected to drop");

        child.SendSigterm();
        var stopped = await child.ExitedWithinAsync(StopBudget);

        stopped.ShouldBeTrue($"watch was still running {StopBudget.TotalSeconds:F0}s after SIGTERM; {child.State()}");
        child.ExitCode.ShouldBe(0, $"SIGTERM took the default action instead of the stop path's 0; {child.State()}");
        File.Exists(claimPath).ShouldBeFalse("the stopped watch left its claim on the store");
        File.Exists(kindBeatPath).ShouldBeFalse(
            "the stopped watch left the kind heartbeat naming a dead pid");
        foreach (var beat in beatsBefore)
        {
            File.Exists(beat).ShouldBeFalse("the stopped watch left its per-pid beat beating");
        }

        child.StdErr.ShouldNotContain("Unhandled exception");
    }

    private sealed class SignalledChild : IDisposable
    {
        private readonly Process _process;
        private readonly object _gate = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();

        private SignalledChild(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
            process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public static SignalledChild Start(string squeezrHome, string workingDir, params string[] args) =>
            new(CliTestProcess.StartLiveChild(squeezrHome, workingDir, args));

        public int ExitCode => _process.ExitCode;

        public int Pid => _process.Id;

        public string StdOut
        {
            get
            {
                lock (_gate)
                {
                    return _stdout.ToString();
                }
            }
        }

        public string StdErr
        {
            get
            {
                lock (_gate)
                {
                    return _stderr.ToString();
                }
            }
        }

        // Process.Kill would answer a different question: it sends SIGKILL, and no handler installed by
        // managed code can take that one.
        public void SendSigterm() => kill(_process.Id, Sigterm);

        public async Task<string> WaitForPathAsync(Func<string?> probe, string what)
        {
            var deadline = DateTime.UtcNow + ReadyBudget;
            while (DateTime.UtcNow < deadline)
            {
                var found = probe();
                if (found is not null)
                {
                    return found;
                }

                if (_process.HasExited)
                {
                    break;
                }

                await Task.Delay(200);
            }

            throw new InvalidOperationException(
                $"{what} within {ReadyBudget.TotalSeconds:F0}s ({State()}); stdout: {StdOut}stderr: {StdErr}");
        }

        public async Task WaitForFilesAsync(IReadOnlyList<string> paths, string what)
        {
            var deadline = DateTime.UtcNow + ReadyBudget;
            while (DateTime.UtcNow < deadline)
            {
                if (paths.All(File.Exists))
                {
                    return;
                }

                if (_process.HasExited)
                {
                    break;
                }

                await Task.Delay(200);
            }

            throw new InvalidOperationException(
                $"{what} within {ReadyBudget.TotalSeconds:F0}s ({State()}); still missing: " +
                $"{string.Join(", ", paths.Where(path => !File.Exists(path)))}; stdout: {StdOut}stderr: {StdErr}");
        }

        public async Task<bool> ExitedWithinAsync(TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    // Bounded on purpose: the parameterless WaitForExit also waits for the redirected pipes
                    // to reach EOF, and a process that inherits the write end can keep it open after the
                    // signalled one is gone.
                    _process.WaitForExit(5000);
                    return true;
                }

                await Task.Delay(200);
            }

            return _process.HasExited;
        }

        public string State() => _process.HasExited
            ? $"it exited with {_process.ExitCode}"
            : "it is still running";

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            _process.WaitForExit(10_000);
            _process.Dispose();
        }

        private void Append(StringBuilder target, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                target.AppendLine(line);
            }
        }
    }
}
