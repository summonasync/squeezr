using Spectre.Console;

namespace Squeezr.Cli.Output;

// Spectre re-wraps any line longer than the console width, and the width it settles on
// when no std handle is a tty is 80. That splits contract text — the symbol truncation
// hint arrives as two lines — and makes the byte stream depend on which handle happened
// to be a terminal. Piped output is consumed by other programs, so it gets unlimited
// width and stays one logical line per write.
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
