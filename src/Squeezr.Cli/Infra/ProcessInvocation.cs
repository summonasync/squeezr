namespace Squeezr.Cli.Infra;

public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);
