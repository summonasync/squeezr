using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// Whether the store this verb reached got filled, and with what numbers. A skip reports its reason instead
// of a zero-filled copy block: the reader must be able to tell "nothing was asked" from "everything was
// copied and the source was empty". 'fill' is the one label this surface uses for the object — the verb
// fills a store, and 'copy' names only the mode that got it filled.
public sealed record WorktreeFillReport(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeCopyReport? Copied)
{
    public static WorktreeFillReport From(WorktreeFillOutcome outcome) => new(
        State: outcome.State.ToString(),
        Reason: outcome.Reason,
        Copied: outcome.Copy is null ? null : WorktreeCopyReport.From(outcome.Copy));
}
