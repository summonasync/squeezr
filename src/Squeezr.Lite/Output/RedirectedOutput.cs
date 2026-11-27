using Spectre.Console;

namespace Squeezr.Lite.Output;

public static class RedirectedOutput
{
    public static void ApplyLayout(IAnsiConsole console, bool outputRedirected)
    {
        if (outputRedirected)
        {
            console.Profile.Width = int.MaxValue;
        }
    }
}
