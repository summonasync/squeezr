using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class SupervisionStateTests : IDisposable
{
    private static readonly DateTime Settled = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Read_NoFileAtAll_IsAbsent()
    {
        var snapshot = new SupervisionState(StatePath).Read();

        snapshot.Status.ShouldBe(SupervisionStateStatus.Absent);
        snapshot.Fault.ShouldBeNull();
        snapshot.Desires.ShouldBeEmpty();
        snapshot.Wedged.ShouldBeEmpty();
    }

    [Fact]
    public void Read_UnparseableFile_IsUnreadable_NotAbsent()
    {
        WriteRawState("{ not json at all");

        var snapshot = new SupervisionState(StatePath).Read();

        snapshot.Status.ShouldBe(SupervisionStateStatus.Unreadable);
        snapshot.FilePath.ShouldBe(StatePath);
        snapshot.Fault.ShouldNotBeNull();
        snapshot.Fault.ShouldContain("not valid JSON");
    }

    [Fact]
    public void Read_TruncatedFile_IsUnreadable()
    {
        WriteRawState(string.Empty);

        new SupervisionState(StatePath).Read().Status.ShouldBe(SupervisionStateStatus.Unreadable);
    }

    [Fact]
    public void Read_FileWithoutTheWedgedKey_KeepsEveryRecordedDesire()
    {
        WriteRawState("""
            {
              "stores": { "demo-abc12345": "Stopped", "other-def67890": "Running" }
            }
            """);

        var snapshot = new SupervisionState(StatePath).Read();

        snapshot.Status.ShouldBe(SupervisionStateStatus.Valid);
        snapshot.Desires.Count.ShouldBe(2);
        snapshot.DesireOf("demo-abc12345").ShouldBe(WatchDesire.Stopped);
        snapshot.IsWedged("demo-abc12345").ShouldBeFalse();
    }

    [Fact]
    public void SetWedged_LandsInTheFileBesideTheDesires_AndReloads()
    {
        var state = new SupervisionState(StatePath);
        state.SetDesire("demo-abc12345", WatchDesire.Stopped);

        state.SetWedged("demo-abc12345", true);

        using var parsed = JsonDocument.Parse(File.ReadAllText(StatePath));
        var root = parsed.RootElement;
        root.GetProperty("stores").GetProperty("demo-abc12345").GetString().ShouldBe("Stopped");
        root.GetProperty("wedged").EnumerateArray().Select(entry => entry.GetString()).Single()
            .ShouldBe("demo-abc12345");

        var reloaded = new SupervisionState(StatePath).Read();
        reloaded.Status.ShouldBe(SupervisionStateStatus.Valid);
        reloaded.IsWedged("demo-abc12345").ShouldBeTrue();
        reloaded.DesireOf("demo-abc12345").ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public void SetWedgedFalse_ClearsTheMarker_AndKeepsTheOtherStores()
    {
        var state = new SupervisionState(StatePath);
        state.SetDesire("demo-abc12345", WatchDesire.Stopped);
        state.SetWedged("demo-abc12345", true);
        state.SetWedged("other-def67890", true);

        state.SetWedged("demo-abc12345", false);

        var reloaded = new SupervisionState(StatePath).Read();
        reloaded.IsWedged("demo-abc12345").ShouldBeFalse();
        reloaded.IsWedged("other-def67890").ShouldBeTrue();
        reloaded.DesireOf("demo-abc12345").ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public void WedgedStore_WedgeIsKeyedOrdinal_NotByCase()
    {
        var state = new SupervisionState(StatePath);
        state.SetWedged("demo-abc12345", true);

        new SupervisionState(StatePath).Read().IsWedged("DEMO-ABC12345").ShouldBeFalse();
    }

    [Fact]
    public void FleetWithoutWedges_WritesTheShapeOlderBinariesAlreadyRead()
    {
        new SupervisionState(StatePath).SetDesire("demo-abc12345", WatchDesire.Stopped);

        File.ReadAllText(StatePath).ShouldNotContain("wedged");
    }

    [Fact]
    public void SetDesire_OnUnreadableFile_Refuses_AndTheFileIsNotRebuilt()
    {
        WriteRawState("{\"stores\": oops");

        Should.Throw<SupervisionStateUnreadableException>(
            () => new SupervisionState(StatePath).SetDesire("demo-abc12345", WatchDesire.Stopped));

        File.ReadAllText(StatePath).ShouldBe("{\"stores\": oops");
    }

    [Fact]
    public void SetWedged_OnUnreadableFile_Refuses_AndTheFileIsNotRebuilt()
    {
        WriteRawState("{\"stores\": oops");

        Should.Throw<SupervisionStateUnreadableException>(
            () => new SupervisionState(StatePath).SetWedged("demo-abc12345", true));

        File.ReadAllText(StatePath).ShouldBe("{\"stores\": oops");
    }

    [Fact]
    public void SetWedgedFalse_OnAStoreTheFileDoesNotCarry_WritesNothing()
    {
        WriteRawState("""
            {
              "stores": { "demo-abc12345": "Stopped" }
            }
            """);
        File.SetLastWriteTimeUtc(StatePath, Settled);

        new SupervisionState(StatePath).SetWedged("demo-abc12345", false);

        File.GetLastWriteTimeUtc(StatePath).ShouldBe(Settled);
    }

    private void WriteRawState(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, json);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
