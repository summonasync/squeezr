namespace Squeezr.Lite.Output;

internal static class GuardOutput
{
    internal static void WriteWithheld(IReadOnlyList<string>? warnings)
    {
        if (warnings is null)
        {
            return;
        }

        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }
}
