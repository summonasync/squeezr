namespace Squeezr.Core.Indexing;

public static class BinaryDetector
{
    private const int ProbeSize = 8192;

    public static bool IsBinary(string path)
    {
        Span<byte> probe = stackalloc byte[ProbeSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(probe);
        return probe[..read].IndexOf((byte)0) >= 0;
    }
}
