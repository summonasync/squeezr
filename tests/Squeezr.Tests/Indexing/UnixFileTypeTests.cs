using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

public class UnixFileTypeTests
{
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsRegularFile_FifoWithoutWriter_IsFalse_FastEnoughToProveNoOpen()
    {
        var fifo = Path.Combine(TestDirs.NewTempDir(), "probe.fifo");
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));

        // the answer must come from stat alone: any open() here would hang the suite
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsRegularFile_TextFile_IsTrue_AndDirectory_IsFalse()
    {
        var root = TestDirs.NewTempDir();
        var file = Path.Combine(root, "keep.cs");
        File.WriteAllText(file, "class A { }");

        UnixFileType.IsRegularFile(file).ShouldBeTrue();
        UnixFileType.IsRegularFile(root).ShouldBeFalse();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsRegularFile_MissingPath_IsTrue_LegacyOpenPathHandlesIt()
    {
        UnixFileType.IsRegularFile(Path.Combine(TestDirs.NewTempDir(), "gone.cs")).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FileApis_OnFifo_AreBlindToType_WhichIsWhyTheStatGateExists()
    {
        // pins the platform trap behind the whole fix: no attribute-based filter can see the fifo file
        // type, and File.Exists answers true for it; if a runtime ever exposes the type through these
        // APIs, this assert tells the next reader the stat gate is redundant
        var fifo = Path.Combine(TestDirs.NewTempDir(), "trap.fifo");
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));

        var attributes = File.GetAttributes(fifo);
        (attributes & FileAttributes.ReparsePoint).ShouldBe((FileAttributes)0,
            "if a fifo ever shows up as a reparse point, the old attribute filter was right all along");
        File.Exists(fifo).ShouldBeTrue("File.Exists only checks the path is not a directory");
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse("only stat answers the file type");
    }

    [Fact]
    public void TryDecodeMode_X64Layout_ReadsModeAt24_AndClassifiesAFifo()
    {
        // x86-64: st_nlink owns 16, st_mode owns 24. S_IFIFO|0755 in the mode field, nlink 1 below it.
        var buffer = new byte[256];
        buffer[16] = 0x01;
        buffer[24] = 0xED;
        buffer[25] = 0x10;

        UnixFileType.TryDecodeMode(buffer, out var mode).ShouldBeTrue();
        mode.ShouldBe((ushort)0x10ED);
        (mode & 0xF000).ShouldNotBe((ushort)0x8000, "a fifo is not a regular file");
    }

    [Fact]
    public void TryDecodeMode_Arm64Layout_ReadsModeAt16_NotTheUidAt24()
    {
        // arm64: st_mode owns 16, st_uid owns 24 — a uid of 1000 must not be mistaken for a mode
        var buffer = new byte[256];
        buffer[16] = 0xA4;
        buffer[17] = 0x81;
        buffer[24] = 0xE8;
        buffer[25] = 0x03;

        UnixFileType.TryDecodeMode(buffer, out var mode).ShouldBeTrue();
        mode.ShouldBe((ushort)0x81A4);
    }

    [Fact]
    public void TryDecodeMode_NoLayoutCarriesAFileTypeNibble_ReportsNoProbe()
    {
        var buffer = new byte[256];
        buffer[16] = 0x01;
        buffer[24] = 0x01;

        UnixFileType.TryDecodeMode(buffer, out _).ShouldBeFalse();
    }

    [Fact]
    public void IsRegularFile_StatSymbolMissing_DegradesToOpenAnyway()
    {
        // glibc before 2.33 exports __xstat rather than stat: the missing entry point must answer like a
        // failed probe (legacy open-anyway), not escape into the walk that only asked for a file type
        Should.NotThrow(() =>
            UnixFileType.IsRegularFile("any.cs", (_, _) => throw new EntryPointNotFoundException("stat"))
                .ShouldBeTrue());
        Should.NotThrow(() =>
            UnixFileType.IsRegularFile("any.cs", (_, _) => throw new DllNotFoundException("libc"))
                .ShouldBeTrue());
    }

    [Fact]
    public void IsRegularFile_StatFails_DegradesToOpenAnyway()
    {
        UnixFileType.IsRegularFile("any.cs", (_, _) => -1).ShouldBeTrue();
    }

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);
}
