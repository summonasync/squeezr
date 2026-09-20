namespace Squeezr.Cli.Output;

public sealed record IndexStartedLine(string Type, string Workspace, int? ChangedPaths, string? Provider);
