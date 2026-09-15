namespace Squeezr.Cli.Infra;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}
