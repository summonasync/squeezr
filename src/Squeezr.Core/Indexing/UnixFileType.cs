using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Squeezr.Core.Indexing;

public static class UnixFileType
{
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFileType = 0x8000;

    // Both FileAttributes (Normal for a FIFO on Linux) and File.Exists (true for any non-directory)
    // are blind to file type, so the only honest probe is stat: S_ISREG on st_mode.
    public static bool IsRegularFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        return IsRegularFile(path, ProbeStat);
    }

    // The stat call is a parameter so the layout table and the two failure modes below can be pinned
    // without hunting for a libc that happens to disagree with this machine. Nothing in here touches a
    // platform API by itself: the injected probe does, and its owner is the one that guards for Linux.
    internal static bool IsRegularFile(string path, Func<string, byte[], int> statProbe)
    {
        return TryProbeMode(statProbe, path, out var mode)
            ? (mode & FileTypeMask) == RegularFileType
            : true;
    }

    // Where st_mode sits is a property of the architecture, not of the libc flavor: on x86-64 glibc and
    // musl both follow the kernel's x86-64 layout and put st_mode at 24 (st_nlink owns 16), while arm64
    // follows the asm-generic layout with st_mode at 16 (st_uid owns 24). A read landing on a non-mode
    // field carries no file-type nibble, so an ABI we do not recognize reports "no probe" and the caller
    // degrades to the legacy open-anyway answer instead of silently skipping real files.
    internal static bool TryDecodeMode(byte[] statBuffer, out ushort mode)
    {
        mode = ReadUInt16(statBuffer, 24);
        if ((mode & FileTypeMask) != 0)
        {
            return true;
        }

        mode = ReadUInt16(statBuffer, 16);
        return (mode & FileTypeMask) != 0;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static bool TryProbeMode(Func<string, byte[], int> statProbe, string path, out ushort mode)
    {
        mode = 0;
        var buffer = new byte[256];
        int result;
        try
        {
            result = statProbe(path, buffer);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // glibc before 2.33 exports __xstat rather than stat, so the symbol lookup fails at the call
            // site: that is a missing probe, the same answer a failing stat gives, and it has to degrade
            // to legacy rather than escape into the walk that only asked for a file type
            return false;
        }

        if (result != 0)
        {
            return false;
        }

        return TryDecodeMode(buffer, out mode);
    }

    [SupportedOSPlatform("linux")]
    private static int ProbeStat(string path, byte[] buffer) => stat(path, buffer);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
