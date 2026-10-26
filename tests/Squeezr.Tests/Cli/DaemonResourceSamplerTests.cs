using System.Diagnostics;
using Shouldly;
using Squeezr.Cli.Output;

namespace Squeezr.Tests.Cli;

public sealed class DaemonResourceSamplerTests
{
    private static readonly DaemonProcReading First = new(100, 200);
    private static readonly DaemonProcReading Second = new(250, 256);

    [Fact]
    public void Sample_FromTwoSyntheticReadings_ComputesCpuPercentAcrossOneWindow()
    {
        var reads = 0;

        var sample = DaemonResourceSampler.Sample(
            [4242],
            TimeSpan.FromMilliseconds(300),
            _ => reads++ == 0 ? First : Second);

        sample[4242].CpuPercent.ShouldNotBeNull();
        sample[4242].CpuPercent!.Value.ShouldBeInRange(250, 650);
        sample[4242].RssBytes.ShouldBe(256L * Environment.SystemPageSize);
        reads.ShouldBe(2);
    }

    [Fact]
    public void Sample_OfManyRows_PausesOnceForTheWholePass_NeverPerRow()
    {
        var waits = new List<TimeSpan>();

        DaemonResourceSampler.Sample(
            Enumerable.Range(4000, 30).ToArray(),
            DaemonResourceSampler.Window,
            _ => First,
            waits.Add);

        waits.ShouldBe([DaemonResourceSampler.Window]);
    }

    [Fact]
    public void Sample_WithDuplicatePidsInTheInput_CollapsesToASingleRow()
    {
        var sample = DaemonResourceSampler.Sample([4242, 4242, 4242], DaemonResourceSampler.Window, _ => First);

        sample.Keys.ShouldBe([4242]);
        sample[4242].CpuPercent.ShouldNotBeNull();
    }

    [Fact]
    public void Sample_WithAnUnreadablePid_YieldsNulls_RatherThanZeroOrAThrow()
    {
        var sample = DaemonResourceSampler.Sample([4242], DaemonResourceSampler.Window, _ => null);

        sample[4242].CpuPercent.ShouldBeNull();
        sample[4242].RssBytes.ShouldBeNull();
    }

    [Fact]
    public void Sample_WithThePidDyingInsideTheWindow_YieldsNulls()
    {
        var reads = 0;

        var sample = DaemonResourceSampler.Sample(
            [4242],
            DaemonResourceSampler.Window,
            _ => reads++ == 0 ? First : null);

        sample[4242].CpuPercent.ShouldBeNull();
        sample[4242].RssBytes.ShouldBeNull();
    }

    [Fact]
    public void Sample_WithAnExplodingReader_DegradesThatPidToNulls_AndKeepsTheRest()
    {
        var sample = DaemonResourceSampler.Sample(
            [1, 2],
            DaemonResourceSampler.Window,
            pid => pid == 1 ? throw new InvalidOperationException("proc raced the read") : First);

        sample[1].CpuPercent.ShouldBeNull();
        sample[2].CpuPercent.ShouldNotBeNull();
    }

    [Fact]
    public void Describe_FromTwoSyntheticReadings_ComputesExactPercentAndBytes()
    {
        var sample = DaemonResourceSampler.Describe(
            new DaemonProcReading(100, 200),
            new DaemonProcReading(200, 256),
            TimeSpan.FromSeconds(1));

        sample.CpuPercent.ShouldNotBeNull();
        sample.CpuPercent!.Value.ShouldBe(100d, 0.001d);
        sample.RssBytes.ShouldBe(256L * Environment.SystemPageSize);
    }

    [Fact]
    public void Describe_WithAnIdleDaemon_ReportsARealZero_NotNull()
    {
        var sample = DaemonResourceSampler.Describe(
            new DaemonProcReading(100, 10),
            new DaemonProcReading(100, 10),
            TimeSpan.FromSeconds(1));

        sample.CpuPercent.ShouldNotBeNull();
        sample.CpuPercent!.Value.ShouldBe(0d);
        sample.RssBytes.ShouldBe(10L * Environment.SystemPageSize);
    }

    [Fact]
    public void Describe_WithShrinkingTicks_AfterPidReuse_YieldsNulls()
    {
        var sample = DaemonResourceSampler.Describe(
            new DaemonProcReading(500, 10),
            new DaemonProcReading(300, 12),
            TimeSpan.FromSeconds(1));

        sample.CpuPercent.ShouldBeNull();
        sample.RssBytes.ShouldBeNull();
    }

    [Fact]
    public void Describe_WithEitherReadingMissing_YieldsNulls()
    {
        DaemonResourceSampler.Describe(First, null, TimeSpan.FromSeconds(1)).CpuPercent.ShouldBeNull();
        DaemonResourceSampler.Describe(null, Second, TimeSpan.FromSeconds(1)).RssBytes.ShouldBeNull();
    }

    [Fact]
    public async Task Live_OwnPidAcrossABusyWindow_ReadsSaneCpuAndRss()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var pid = Environment.ProcessId;
        var sampleTask = Task.Run(() => DaemonResourceSampler.Sample([pid], TimeSpan.FromMilliseconds(300)));
        var busyStartedAt = Stopwatch.GetTimestamp();
        await Task.Run(() =>
        {
            while (Stopwatch.GetElapsedTime(busyStartedAt) < TimeSpan.FromMilliseconds(450))
            {
            }
        });

        var sample = (await sampleTask)[pid];

        sample.CpuPercent.ShouldNotBeNull();
        sample.CpuPercent!.Value.ShouldBeGreaterThan(20);
        sample.CpuPercent!.Value.ShouldBeLessThanOrEqualTo(100 * Environment.ProcessorCount);
        sample.RssBytes.ShouldNotBeNull();
        using var process = Process.GetCurrentProcess();
        sample.RssBytes!.Value.ShouldBeInRange(process.WorkingSet64 / 2, process.WorkingSet64 * 3 / 2);
    }
}
