using Squeezr.Core.Configuration;

namespace Squeezr.Lite.Config;

public sealed record LiteConfig(SqueezrConfig? Config, IReadOnlyList<string> Warnings)
{
    public static readonly LiteConfig Missing = new(null, []);
}
