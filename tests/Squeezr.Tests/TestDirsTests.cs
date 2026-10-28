using Shouldly;

namespace Squeezr.Tests;

[Collection("ProcessGlobals")]
public sealed class TestDirsTests : IDisposable
{
    private const string TempRootVariable = "SQUEEZR_TEST_TMPROOT";

    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(TempRootVariable);

    public void Dispose() => Environment.SetEnvironmentVariable(TempRootVariable, _previousOverride);

    [Fact]
    public void NewTempDir_RegistersItsLeafForTeardown()
    {
        var leaf = TestDirs.NewTempDir();

        TestDirs.TryRemoveRegisteredLeaf(leaf).ShouldBeTrue();
        Directory.Exists(leaf).ShouldBeFalse();
    }

    [Fact]
    public void NewNonHomeScratchDir_RegistersItsLeafForTeardown()
    {
        var leaf = TestDirs.NewNonHomeScratchDir();

        TestDirs.TryRemoveRegisteredLeaf(leaf).ShouldBeTrue();
        Directory.Exists(leaf).ShouldBeFalse();
    }

    [Fact]
    public void TryRemoveRegisteredLeaf_RefusesPathsOutsideTheRegistry()
    {
        using var outsider = new OutsiderDir();

        TestDirs.TryRemoveRegisteredLeaf(outsider.Path).ShouldBeFalse();
        Directory.Exists(outsider.Path).ShouldBeTrue();
    }

    [Fact]
    public void NewTempDir_HonorsTmpRootOverride()
    {
        var overrideRoot = Directory.CreateTempSubdirectory("squeezr-tmproot-").FullName;
        Environment.SetEnvironmentVariable(TempRootVariable, overrideRoot);
        try
        {
            var leaf = TestDirs.NewTempDir();

            leaf.ShouldStartWith(Path.Combine(overrideRoot, "squeezr-tests"));
            Directory.Exists(leaf).ShouldBeTrue();

            TestDirs.TryRemoveRegisteredLeaf(leaf).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TempRootVariable, _previousOverride);
            try
            {
                Directory.Delete(Path.Combine(overrideRoot, "squeezr-tests"), recursive: false);
                Directory.Delete(overrideRoot, recursive: false);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class OutsiderDir : IDisposable
    {
        public OutsiderDir() => Path = Directory.CreateTempSubdirectory("testdirs-outsider-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
