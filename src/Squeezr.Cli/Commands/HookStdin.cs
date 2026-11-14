namespace Squeezr.Cli.Commands;

// The one stdin reader both hook verbs share: a hook reads a bounded payload and degrades to the fail-open
// empty path rather than trusting a size it did not measure.
public static class HookStdin
{
    // Oversized input is treated as unreadable rather than truncated: hooks stay advisory, and an oversized
    // payload degrades to the fail-open empty path. The rest of the stream is drained into a fixed sink so a
    // large writer never blocks on a full pipe, and allocation stays bounded by the buffer above.
    public static string Read(int maxChars)
    {
        var buffer = new char[maxChars + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = Console.In.ReadBlock(buffer, read, buffer.Length - read)) > 0)
        {
            read += chunk;
        }

        if (read <= maxChars)
        {
            return new string(buffer, 0, read);
        }

        var sink = new char[4096];
        while (Console.In.ReadBlock(sink, 0, sink.Length) > 0)
        {
        }

        return string.Empty;
    }
}
