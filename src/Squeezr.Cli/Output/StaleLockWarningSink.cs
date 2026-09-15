namespace Squeezr.Cli.Output;

internal static class StaleLockWarningSink
{
    public static void ToStderr(string message) => Console.Error.WriteLine($"warning: {message}");
}
