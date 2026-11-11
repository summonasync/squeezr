using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// Whether the store this verb reached got filled, and with what numbers. A skip reports its reason instead
// of a zero-filled copy block: the reader must be able to tell "nothing was asked" from "everything was
// copied and the source was empty". 'fill' is the one label this surface uses for the object — the verb
// fills a store, and 'copy' names only the mode that got it filled.
//
// 'vectors' is absent when this pass had no vector half to ask for — a source whose database holds pending
// rows owns no points to inherit — and present as either the three agreed numbers or a refusal with its
// reason. The reader has to be able to tell "no vectors existed" from "the vector store was not in the room
// and the copied rows still claim they are embedded", and only this field tells them apart.
public sealed record WorktreeFillReport(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeCopyReport? Copied,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeVectorsCopyReport? Vectors)
{
    public static WorktreeFillReport From(WorktreeFillOutcome outcome) => new(
        State: outcome.State.ToString(),
        Reason: outcome.Reason,
        Copied: outcome.Copy is null ? null : WorktreeCopyReport.From(outcome.Copy),
        Vectors: outcome.Vectors is null ? null : WorktreeVectorsCopyReport.From(outcome.Vectors));
}
