namespace Squeezr.Lite.Output;

public sealed record JsonErrorBody(
    string Code,
    string Message,
    IReadOnlyList<string>? RemovedPaths = null,
    IReadOnlyList<string>? NextSteps = null);
