using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class PassPhaseStampFileTests
{
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void WriteThenTryRead_RoundTripsStamp()
    {
        var stamp = new PassPhaseStamp(4242, PassPhaseStamp.RunningPhase, started, started, 7);

        PassPhaseStampFile.Write(_storeDir, stamp);

        var read = PassPhaseStampFile.TryRead(_storeDir);
        read.ShouldNotBeNull();
        read.Pid.ShouldBe(4242);
        read.Phase.ShouldBe(PassPhaseStamp.RunningPhase);
        read.StartedAt.ShouldBe(started);
        read.LastProgressAt.ShouldBe(started);
        read.BatchSize.ShouldBe(7);
    }

    [Fact]
    public void TryRead_WithoutStampFile_ReturnsNull()
    {
        PassPhaseStampFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void Clear_RemovesStamp_AndSecondClearIsQuiet()
    {
        PassPhaseStampFile.Write(_storeDir, Stuck());

        PassPhaseStampFile.Clear(_storeDir);

        PassPhaseStampFile.TryRead(_storeDir).ShouldBeNull();
        Should.NotThrow(() => PassPhaseStampFile.Clear(_storeDir));
    }

    [Fact]
    public void Write_OverwritesPreviousPhase_AtomicMove()
    {
        PassPhaseStampFile.Write(_storeDir, Stuck());
        PassPhaseStampFile.Write(_storeDir, new PassPhaseStamp(7, PassPhaseStamp.RunningPhase, started, started, 1));

        PassPhaseStampFile.TryRead(_storeDir)!.Phase.ShouldBe(PassPhaseStamp.RunningPhase);
        Directory.GetFiles(_storeDir, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void ClearIfWriterGone_LiveWritersRecord_SurvivesAndReportsNothing()
    {
        // the whole point of W4: a second watch engine must not get to erase the wedge evidence of a live one
        PassPhaseStampFile.Write(_storeDir, Stuck());

        PassPhaseStampFile.ClearIfWriterGone(_storeDir, _ => true).ShouldBeFalse();

        PassPhaseStampFile.TryRead(_storeDir)!.Phase.ShouldBe(PassPhaseStamp.StuckPhase);
    }

    [Fact]
    public void ClearIfWriterGone_GoneWriter_IsConsumedAndReportedAsUnfinishedWork()
    {
        PassPhaseStampFile.Write(_storeDir, Stuck());

        PassPhaseStampFile.ClearIfWriterGone(_storeDir, _ => false).ShouldBeTrue();

        PassPhaseStampFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void ClearIfWriterGone_OwnPid_IsOursToConsume()
    {
        PassPhaseStampFile.Write(
            _storeDir, new PassPhaseStamp(Environment.ProcessId, PassPhaseStamp.RunningPhase, started, started, 1));

        PassPhaseStampFile.ClearIfWriterGone(_storeDir, _ => true).ShouldBeTrue();

        PassPhaseStampFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void ClearIfWriterGone_HandOffOfALivePid_IsStillConsumed()
    {
        // an interrupted record is a to-do note, not evidence: whoever wrote it, the pass never finished
        PassPhaseStampFile.Write(
            _storeDir, new PassPhaseStamp(4242, PassPhaseStamp.InterruptedPhase, started, started, 2));

        PassPhaseStampFile.ClearIfWriterGone(_storeDir, _ => true).ShouldBeTrue();

        PassPhaseStampFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void ClearIfWriterGone_WithoutARecord_ReportsNothing()
    {
        PassPhaseStampFile.ClearIfWriterGone(_storeDir, _ => true).ShouldBeFalse();
    }

    private static readonly DateTime started = new(2026, 10, 3, 17, 19, 8, DateTimeKind.Utc);

    private PassPhaseStamp Stuck() => new(69168, PassPhaseStamp.StuckPhase, started, started, 3);
}
