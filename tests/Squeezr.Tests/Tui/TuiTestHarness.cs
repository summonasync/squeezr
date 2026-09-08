using Spectre.Console.Testing;

namespace Squeezr.Tests.Tui;

internal static class TuiTestHarness
{
    public static TestConsole InteractiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        return console;
    }

    public static void PushMenuChoice(TestConsole console, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }
}
