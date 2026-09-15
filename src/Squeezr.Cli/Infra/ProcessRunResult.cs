namespace Squeezr.Cli.Infra;

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);
