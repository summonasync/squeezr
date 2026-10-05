namespace Squeezr.Cli.Output;

// One sentence for a withheld tally in every human surface that drops rows: the producer is
// SecretGuardReader, so map/symbol/refs cannot drift apart in wording the way --json's field already
// proved they could not.
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
