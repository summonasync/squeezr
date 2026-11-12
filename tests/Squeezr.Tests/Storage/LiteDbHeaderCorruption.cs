using System.Text;
using Shouldly;

namespace Squeezr.Tests.Storage;

// The store LiteDB refuses to report as a format error: the magic phrase it looks for stays intact, and the
// header fields written after it become garbage.
internal static class LiteDbHeaderCorruption
{
    private const string Magic = "** This is a LiteDB file **";

    internal static void AfterTheMagic(string metaDbPath, int length = 64)
    {
        var buffer = File.ReadAllBytes(metaDbPath);
        var header = Encoding.ASCII.GetString(buffer, 0, Math.Min(buffer.Length, 4096));
        var at = header.IndexOf(Magic, StringComparison.Ordinal);
        at.ShouldBeGreaterThan(-1, $"{metaDbPath} carries no {Magic}, so this is not the header form");

        for (var i = at + Magic.Length; i < at + Magic.Length + length && i < buffer.Length; i++)
        {
            buffer[i] = 0xFF;
        }

        File.WriteAllBytes(metaDbPath, buffer);
    }
}
