namespace Squeezr.Cli.Output;

// The fill's human line for the semantic arm. Three states, and the middle one is the reason this is a
// function rather than a ternary: 'copied: false' covers both a pass that wrote nothing and a pass that
// wrote everything it could name, and a line that says 'not copied' over points that landed is a report that
// reads wrong at the terminal — while the JSON surface and the warning channel both carry the distinction.
internal static class WorktreeVectorsFillLine
{
    internal static string Build(WorktreeVectorsCopyReport? vectors) => vectors switch
    {
        null => "  fill: vectors [grey]the main worktree holds no embedded chunks — nothing to inherit[/]",
        { Copied: true } =>
            $"  fill: vectors {vectors.SourcePoints} → {vectors.CopiedPoints} → {vectors.TargetPoints} points " +
            "(copied as they stand — no embedding ran here)",
        { CopiedPoints: > 0 } =>
            $"  fill: vectors [yellow]partly[/] — {vectors.CopiedPoints} of {vectors.SourcePoints} source " +
            $"point(s) landed (the worktree's collection counts {vectors.TargetPoints}); the warning line " +
            "names what this pass did not take, and this store still claims its chunks are embedded",
        _ =>
            "  fill: vectors [yellow]not copied[/] — the warning line names why, and this store still claims " +
            "its chunks are embedded",
    };
}
