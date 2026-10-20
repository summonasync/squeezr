namespace Squeezr.Cli.Output;

// RemovedPaths stays null for every failure that removed nothing: the key is omitted from the envelope,
// so the 035 shape of existing errors does not change. NextSteps rides the same omission rule — only the
// codes with a known remedy carry it, and then it is the same block the MCP envelope serves.
public sealed record JsonErrorBody(
    string Code,
    string Message,
    IReadOnlyList<string>? RemovedPaths = null,
    IReadOnlyList<string>? NextSteps = null);
