using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class SupervisionStateTests : IDisposable
{
    private readonly string _dir = TestDirs.NewTempDir();

    private string StatePath => Path.Combine(_dir, "watchd", "state.json");

    [Fact]
    public void UnknownStore_DefaultsToRunning()
    {
        new SupervisionState(StatePath).DesireOf("demo-abc12345").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public void DesiredStopped_SurvivesRecreation()
    {
        new SupervisionState(StatePath).SetDesire("demo-abc12345", WatchDesire.Stopped);

        new SupervisionState(StatePath).DesireOf("demo-abc12345").ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public void StoresKeepIndependentDesires_AcrossRecreation()
    {
        var state = new SupervisionState(StatePath);
        state.SetDesire("demo-abc12345", WatchDesire.Stopped);
        state.SetDesire("other-def67890", WatchDesire.Running);

        var reloaded = new SupervisionState(StatePath);
        reloaded.DesireOf("demo-abc12345").ShouldBe(WatchDesire.Stopped);
        reloaded.DesireOf("other-def67890").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public void OverwritingADesire_ReplacesTheOldOne()
    {
        var state = new SupervisionState(StatePath);
        state.SetDesire("demo-abc12345", WatchDesire.Stopped);
        state.SetDesire("demo-abc12345", WatchDesire.Running);

        new SupervisionState(StatePath).DesireOf("demo-abc12345").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public void UnreadableFile_FallsBackToRunningDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{ not json at all");

        new SupervisionState(StatePath).DesireOf("demo-abc12345").ShouldBe(WatchDesire.Running);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
