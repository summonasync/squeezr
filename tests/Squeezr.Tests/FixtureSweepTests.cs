using Shouldly;

namespace Squeezr.Tests;

public class FixtureSweepTests
{
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(6);
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SweepStaleRoots_DeletesOnlyStaleGuidLeaves()
    {
        var baseDir = TestDirs.NewTempDir();
        var stale = Leaf(baseDir, ageHours: 7);
        var fresh = Leaf(baseDir, ageHours: 1);
        var nonGuid = Leaf(baseDir, ageHours: 7, name: "missing-home-082");

        FixtureSweep.SweepStaleRoots(baseDir, StaleAge, Now);

        Directory.Exists(stale).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
        Directory.Exists(nonGuid).ShouldBeTrue();
        Directory.Exists(baseDir).ShouldBeTrue();
    }

    [Fact]
    public void SweepStaleRoots_DeletesStaleLeafWithContentRecursively()
    {
        var baseDir = TestDirs.NewTempDir();
        var stale = Directory.CreateDirectory(Path.Combine(baseDir, Guid.NewGuid().ToString("N")));
        var nested = Directory.CreateDirectory(Path.Combine(stale.FullName, "store", "deep"));
        File.WriteAllText(Path.Combine(nested.FullName, "meta.db"), "payload");
        Directory.SetLastWriteTimeUtc(stale.FullName, Now - TimeSpan.FromHours(30));

        FixtureSweep.SweepStaleRoots(baseDir, StaleAge, Now);

        Directory.Exists(stale.FullName).ShouldBeFalse();
    }

    [Fact]
    public void SweepStaleRoots_AbsentBase_IsSilentNoOp()
    {
        var absent = Path.Combine(TestDirs.NewTempDir(), "never-created");

        FixtureSweep.SweepStaleRoots(absent, StaleAge, Now);

        Directory.Exists(absent).ShouldBeFalse();
    }

    [Fact]
    public void SweepStaleRoots_LeafExactlyAtThreshold_Survives()
    {
        var baseDir = TestDirs.NewTempDir();
        var boundary = Leaf(baseDir, ageHours: 6);

        FixtureSweep.SweepStaleRoots(baseDir, StaleAge, Now);

        Directory.Exists(boundary).ShouldBeTrue();
    }

    private static string Leaf(string baseDir, int ageHours, string? name = null)
    {
        var leaf = Directory.CreateDirectory(Path.Combine(baseDir, name ?? Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(leaf.FullName, "marker.txt"), "x");
        Directory.SetLastWriteTimeUtc(leaf.FullName, Now - TimeSpan.FromHours(ageHours));
        return leaf.FullName;
    }
}
