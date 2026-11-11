namespace Squeezr.Tests.Infra.Autostart;

// A unit names a program the init system execs on its own, and the install seam checks launchability
// before it writes anything, so a test that reaches the seam has to hand over a file that really can
// be exec'ed. The mode rules are unix semantics — on a Windows host the call is skipped and the
// guard answers from the file extension instead.
internal static class TestProgram
{
    internal const UnixFileMode ReadWriteExecute = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    internal const UnixFileMode ReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal const UnixFileMode UserReadWriteExecute = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;

    internal static string Executable(string fileName) => Write(fileName, ReadWriteExecute);

    internal static string NotExecutable(string fileName) => Write(fileName, ReadWrite);

    internal static string Write(string fileName, UnixFileMode mode)
    {
        var path = Path.Combine(TestDirs.NewTempDir(), fileName);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }

        return path;
    }
}
