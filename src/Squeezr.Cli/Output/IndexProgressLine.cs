namespace Squeezr.Cli.Output;

public sealed record IndexProgressLine(string Type, string Repo, string Phase, int Total, int? Done = null);
