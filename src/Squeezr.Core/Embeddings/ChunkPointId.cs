using System.Security.Cryptography;
using System.Text;

namespace Squeezr.Core.Embeddings;

public static class ChunkPointId
{
    public static ulong FromHash(string chunkHash)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunkHash));
        return BitConverter.ToUInt64(hash, 0);
    }
}
