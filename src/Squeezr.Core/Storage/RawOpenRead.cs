using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Squeezr.Core.Storage;

// Reading index.lock while a live holder keeps its FileShare.None handle open is impossible through any
// managed open: on unix .NET emulates FileShare with advisory flock and asks for LOCK_SH whatever share
// mode the caller names, and the holder's LOCK_EX refuses it with EAGAIN. The owner pid is bytes the
// kernel hands out to a plain open(2) that claims nothing, so that is the only read that can name a
// holder in the incident the report exists for.
internal static class RawOpenRead
{
    private const int ReadOnly = 0;

    // The syscalls are retried on EINTR inside this method rather than in helpers: a daemon takes signals,
    // and a signal landing in open/read answers EINTR instead of a result. The retry budget is small and
    // the guard belongs here too, because moving a call out of the platform check turns it into a
    // platform-agnostic call site the analyzer is right to flag.
    private const int Interrupted = 4;
    private const int SignalRetries = 3;

    internal static bool TryReadAllText(string path, int maxBytes, out string text)
    {
        text = string.Empty;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        int descriptor;
        try
        {
            var attempts = 0;
            while (true)
            {
                descriptor = Open(path, ReadOnly, 0);
                if (descriptor >= 0)
                {
                    break;
                }

                if (Marshal.GetLastWin32Error() != Interrupted)
                {
                    return false;
                }

                if (attempts++ >= SignalRetries)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }

        if (descriptor < 0)
        {
            return false;
        }

        try
        {
            var buffer = new byte[maxBytes];
            int read;
            var attempts = 0;
            while (true)
            {
                read = Read(descriptor, buffer, buffer.Length);
                if (read >= 0)
                {
                    break;
                }

                if (Marshal.GetLastWin32Error() != Interrupted)
                {
                    return false;
                }

                if (attempts++ >= SignalRetries)
                {
                    return false;
                }
            }

            text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return true;
        }
        finally
        {
            Close(descriptor);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("osx")]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, int mode);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("osx")]
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern int Read(int fd, byte[] buffer, int count);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("osx")]
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
