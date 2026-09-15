using Shouldly;
using Squeezr.Core.Infra;

namespace Squeezr.Tests.Infra;

public sealed class RocmDeviceProbeTests
{
    [Fact]
    public void AreAvailable_WhenKfdDeviceAndDriDirectoryExist_ReturnsTrue()
    {
        var dir = TestDirs.NewTempDir();
        var kfd = Path.Combine(dir, "kfd");
        File.WriteAllText(kfd, string.Empty);
        var dri = Path.Combine(dir, "dri");
        Directory.CreateDirectory(dri);

        new RocmDeviceProbe(kfd, dri).AreAvailable().ShouldBeTrue();
    }

    [Fact]
    public void AreAvailable_WhenKfdDeviceMissing_ReturnsFalse()
    {
        var dir = TestDirs.NewTempDir();
        var dri = Path.Combine(dir, "dri");
        Directory.CreateDirectory(dri);

        new RocmDeviceProbe(Path.Combine(dir, "kfd"), dri).AreAvailable().ShouldBeFalse();
    }

    [Fact]
    public void AreAvailable_WhenDriDirectoryMissing_ReturnsFalse()
    {
        var dir = TestDirs.NewTempDir();
        var kfd = Path.Combine(dir, "kfd");
        File.WriteAllText(kfd, string.Empty);

        new RocmDeviceProbe(kfd, Path.Combine(dir, "dri")).AreAvailable().ShouldBeFalse();
    }

    [Fact]
    public void AreAvailable_WhenDriPathIsAFile_ReturnsFalse()
    {
        var dir = TestDirs.NewTempDir();
        var kfd = Path.Combine(dir, "kfd");
        File.WriteAllText(kfd, string.Empty);
        var dri = Path.Combine(dir, "dri");
        File.WriteAllText(dri, string.Empty);

        new RocmDeviceProbe(kfd, dri).AreAvailable().ShouldBeFalse();
    }
}
