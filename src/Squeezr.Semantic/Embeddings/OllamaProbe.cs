using Squeezr.Core.Configuration;

namespace Squeezr.Semantic.Embeddings;

public static class OllamaProbe
{
    private const string Ipv4LoopbackHost = "127.0.0.1";

    // Windows keeps retrying the refused IPv6 SYN for ~3s before falling through to IPv4;
    // anything shorter kills the probe before the stack ever reaches the IPv4 listener.
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public static Uri ProbeUri(string endpoint, string path)
    {
        var builder = new UriBuilder(endpoint.TrimEnd('/') + "/" + path);
        if (builder.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            builder.Host = Ipv4LoopbackHost;
        return builder.Uri;
    }
}
