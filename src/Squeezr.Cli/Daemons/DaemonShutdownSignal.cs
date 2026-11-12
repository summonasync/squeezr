using System.Runtime.InteropServices;

namespace Squeezr.Cli.Daemons;

// A process started by an init system never sees Ctrl+C: there is no terminal to raise it, and the stop
// command sends SIGTERM to every process still in the unit. Without a registration here the runtime takes
// the signal's default action and the process dies mid-sweep, leaving its lock or owner marker and its
// beats behind for the staleness rules to sort out.
public static class DaemonShutdownSignal
{
    // The caller has to keep the returned handle: the runtime holds a PosixSignalRegistration weakly, so a
    // registration nothing references anymore stops firing without saying anything.
    public static IDisposable Watch(CancellationTokenSource cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            return NoStopSignal.Shared;
        }

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Cancel(cancellation);
        });
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The handle is disposed with the command, and the command disposes its source on the way out,
            // so a signal that is already in flight can reach a source that is gone. An exception thrown on
            // the runtime's signal thread takes the process down with it, which is the opposite of the stop
            // this type exists to give.
        }
    }

    private sealed class NoStopSignal : IDisposable
    {
        public static readonly NoStopSignal Shared = new();

        public void Dispose()
        {
        }
    }
}
