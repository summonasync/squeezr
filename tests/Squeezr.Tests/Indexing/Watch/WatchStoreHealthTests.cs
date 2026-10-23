using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class WatchStoreHealthTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly int DeadPid = int.MaxValue;
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void NoMarkerAndNoBeats_AnswersNone_WithNoIndexerRunning()
    {
        var health = WatchStoreHealth.Probe(_storeDir, Now, _ => true);

        health.State.ShouldBe(WatchIndexerState.None);
        health.IndexerRunning.ShouldBeFalse();
        health.Observers.ShouldBe(0);
    }

    [Fact]
    public void FreshOwnerBeatUnderLivePid_IsAlive_AndItsOwnBeatIsNotCountedAsObserver()
    {
        WriteClaim(new WatchOwnerMarker(4242, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, Environment.MachineName));

        var health = WatchStoreHealth.Probe(_storeDir, Now, pid => pid == 4242);

        health.State.ShouldBe(WatchIndexerState.Alive);
        health.OwnerPid.ShouldBe(4242);
        health.OwnerKind.ShouldBe(DaemonHeartbeat.DefaultKind);
        health.IndexerRunning.ShouldBeTrue();
        health.Observers.ShouldBe(0, "the owner answers the roster once and owns that row");
    }

    [Fact]
    public void LiveBeatsBesideTheOwner_CountAsObservers_NotOwners()
    {
        WriteClaim(new WatchOwnerMarker(4242, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, Environment.MachineName));
        WriteBeat(new DaemonHeartbeat(4343, DaemonHeartbeat.ServeWatchKind, Now, Now, Environment.MachineName));
        WriteBeat(new DaemonHeartbeat(4444, DaemonHeartbeat.ServeWatchKind, Now, Now, Environment.MachineName));

        var health = WatchStoreHealth.Probe(_storeDir, Now, pid => pid is 4242 or 4343 or 4444);

        health.State.ShouldBe(WatchIndexerState.Alive);
        health.Observers.ShouldBe(2);
    }

    [Fact]
    public void StaleBeatUnderLivePid_IsWedged_RunningFalse()
    {
        WriteClaim(new WatchOwnerMarker(
            4242, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now.AddHours(-2), Now.AddMinutes(-5)));

        var health = WatchStoreHealth.Probe(_storeDir, Now, pid => pid == 4242);

        health.State.ShouldBe(WatchIndexerState.Wedged);
        health.IndexerRunning.ShouldBeFalse();
    }

    [Fact]
    public void GoneOwnerPid_IsGone_RunningFalse()
    {
        WriteClaim(new WatchOwnerMarker(DeadPid, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));

        var health = WatchStoreHealth.Probe(_storeDir, Now, _ => false);

        health.State.ShouldBe(WatchIndexerState.Gone);
        health.IndexerRunning.ShouldBeFalse();
    }

    [Fact]
    public void ForeignMarkerInsideItsWindow_IsTheIndexer_OutsideItTheStoreIsFree()
    {
        WriteClaim(new WatchOwnerMarker(4242, "elsewhere", DaemonHeartbeat.DefaultKind, Now, Now));

        WatchStoreHealth.Probe(_storeDir, Now, _ => true).State.ShouldBe(WatchIndexerState.Foreign);
        WatchStoreHealth.Probe(_storeDir, Now, _ => true).IndexerRunning.ShouldBeTrue();

        File.Delete(WatchOwnerMarker.PathFor(_storeDir));
        WriteClaim(new WatchOwnerMarker(
            4242, "elsewhere", DaemonHeartbeat.DefaultKind, Now, Now.AddMinutes(-10)));

        var aged = WatchStoreHealth.Probe(_storeDir, Now, _ => true);
        aged.State.ShouldBe(WatchIndexerState.Gone);
        aged.IndexerRunning.ShouldBeFalse();
    }

    [Theory]
    [InlineData(WatchIndexerState.None)]
    [InlineData(WatchIndexerState.Gone)]
    [InlineData(WatchIndexerState.Wedged)]
    public void NoWorkingIndexer_GetTheExactFleetRestartCommand(WatchIndexerState state)
    {
        var health = new WatchStoreHealth(state, 4242, DaemonHeartbeat.DefaultKind, Environment.MachineName, 1);

        var diagnostics = StoreWatchDiagnostics.From(health, "demo", "demo-abc12345")!;

        diagnostics.OwnerState.ShouldBe(state.ToString().ToLowerInvariant());
        diagnostics.StartCommand.ShouldBe("squeezr fleet restart demo-abc12345");
    }

    [Fact]
    public void RunningIndexer_CarriesNoStartCommand()
    {
        var health = new WatchStoreHealth(WatchIndexerState.Alive, 4242, DaemonHeartbeat.DefaultKind, "box", 0);

        StoreWatchDiagnostics.From(health, "demo", "demo-abc12345")!.StartCommand.ShouldBeNull();
    }

    [Fact]
    public void OddStoreKey_IsShellQuotedInThePasteCommand()
    {
        var health = new WatchStoreHealth(WatchIndexerState.None, null, null, null, 0);

        StoreWatchDiagnostics.From(health, "my repo", "my repo-deadbeef")!.StartCommand
            .ShouldBe("squeezr fleet restart 'my repo-deadbeef'");
    }

    [Fact]
    public void DiagnosticsJson_KeepsContractKeysVisible_EvenWithNoOwner()
    {
        var diagnostics = StoreWatchDiagnostics.From(
            new WatchStoreHealth(WatchIndexerState.None, null, null, null, 0), "demo", "demo-abc12345");

        var serialized = JsonSerializer.Serialize(diagnostics, SqueezrJson.OutputOptions);
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;

        root.GetProperty("name").GetString().ShouldBe("demo");
        root.GetProperty("ownerState").GetString().ShouldBe("none");
        root.TryGetProperty("ownerPid", out var ownerPid).ShouldBeTrue();
        ownerPid.ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("observers").GetInt32().ShouldBe(0);
        root.GetProperty("startCommand").GetString().ShouldBe("squeezr fleet restart demo-abc12345");
    }

    private void WriteClaim(WatchOwnerMarker marker) =>
        File.WriteAllText(WatchOwnerMarker.PathFor(_storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private void WriteBeat(DaemonHeartbeat beat)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(_storeDir, beat.Kind, beat.Pid),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
