using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class PassBudgetLadderTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(60);

    [Fact]
    public void NoteOverrun_DoublesTheArmedBudget_AndNoteCompletedSnapsItBack()
    {
        var ladder = new PassBudgetLadder(Floor, Ceiling);

        ladder.Armed.ShouldBe(Floor);
        ladder.NoteOverrun();
        ladder.Armed.ShouldBe(TimeSpan.FromMinutes(20));
        ladder.NoteOverrun();
        ladder.Armed.ShouldBe(TimeSpan.FromMinutes(40));
        ladder.NoteCompleted();
        ladder.Armed.ShouldBe(Floor, "a repo that fits its budget must not keep the escalated window");
    }

    [Fact]
    public void NoteOverrun_StopsAtTheCeiling()
    {
        var ladder = new PassBudgetLadder(Floor, Ceiling);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            ladder.NoteOverrun();
        }

        ladder.Armed.ShouldBe(Ceiling, "an endless escalation would lock the store for hours per cycle");
    }

    [Fact]
    public void NoteOverrun_OnTheImmediateAbortSeam_StaysImmediate()
    {
        // TimeSpan.Zero is the test seam meaning "cancel at the first boundary": escalation must never
        // un-arm it into a real timer
        var ladder = new PassBudgetLadder(TimeSpan.Zero, Ceiling);

        ladder.NoteOverrun();
        ladder.NoteOverrun();

        ladder.Armed.ShouldBe(TimeSpan.Zero);
    }
}
