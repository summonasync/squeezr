using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class WatchOwnerMarkerTests
{
    private static readonly DateTime Beat = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PathFor_PlacesOneMarkerPerStoreDir() =>
        WatchOwnerMarker.PathFor("/store")
            .ShouldBe(Path.Combine("/store", "watch.owner"));

    [Fact]
    public void ForeignHostStaleWindow_IsTheDocumentedMultipleOfTheHeartbeatThreshold()
    {
        WatchOwnerMarker.ForeignHostStaleWindowMultiplier.ShouldBe(4);
        WatchOwnerMarker.ForeignHostStaleWindow
            .ShouldBe(DaemonHeartbeat.StaleThreshold * WatchOwnerMarker.ForeignHostStaleWindowMultiplier);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(30, false)]
    [InlineData(31, true)]
    [InlineData(120, true)]
    public void IsBeatStale_FlipsAtTheHeartbeatThreshold(int ageSeconds, bool expected) =>
        Marker(ageSeconds).IsBeatStale(Beat).ShouldBe(expected);

    [Theory]
    [InlineData(31, false)]
    [InlineData(120, false)]
    [InlineData(121, true)]
    public void IsPastForeignWindow_FlipsAtTheExtendedThreshold(int ageSeconds, bool expected) =>
        Marker(ageSeconds).IsPastForeignWindow(Beat).ShouldBe(expected);

    [Theory]
    [InlineData(700, "box-a", true)]
    [InlineData(700, "BOX-A", true)]
    [InlineData(700, "box-b", false)]
    [InlineData(701, "box-a", false)]
    public void IsOwnedBy_MatchesPidAndHostCaseInsensitively(int probePid, string probeHost, bool expected) =>
        Marker(0).IsOwnedBy(probePid, probeHost).ShouldBe(expected);

    [Fact]
    public void AsHeartbeat_HandsTheExistingStalenessRulePidKindAndTimestamps()
    {
        var marker = new WatchOwnerMarker(700, "box-a", DaemonHeartbeat.ServeWatchKind, Beat, Beat);

        var heartbeat = marker.AsHeartbeat();

        heartbeat.Pid.ShouldBe(700);
        heartbeat.Kind.ShouldBe(DaemonHeartbeat.ServeWatchKind);
        heartbeat.StartedAt.ShouldBe(Beat);
        heartbeat.LastBeatAt.ShouldBe(Beat);
    }

    [Fact]
    public void Marker_SurvivesTheFileSerializerWithoutShiftingTheInstant()
    {
        var marker = new WatchOwnerMarker(
            700, "box-a", DaemonHeartbeat.DefaultKind, Beat - TimeSpan.FromMinutes(3), Beat);

        var parsed = JsonSerializer.Deserialize<WatchOwnerMarker>(
            JsonSerializer.Serialize(marker, SqueezrJson.FileOptions), SqueezrJson.FileOptions).ShouldNotBeNull();

        parsed.ShouldBe(marker);
        parsed.LastBeatAt.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Marker_WritesTheFieldsDownstreamReadersNeed()
    {
        var json = JsonSerializer.Serialize(
            new WatchOwnerMarker(700, "box-a", DaemonHeartbeat.DefaultKind, Beat, Beat), SqueezrJson.FileOptions);

        foreach (var field in new[] { "pid", "host", "kind", "startedAt", "lastBeatAt" })
        {
            json.ShouldContain($"\"{field}\":");
        }
    }

    private static WatchOwnerMarker Marker(int ageSeconds, int pid = 700, string host = "box-a") =>
        new(pid, host, DaemonHeartbeat.DefaultKind, Beat - TimeSpan.FromSeconds(ageSeconds),
            Beat - TimeSpan.FromSeconds(ageSeconds));
}
