using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

[JsonConverter(typeof(WatchConfigConverter))]
public sealed record WatchConfig
{
    public bool Enabled { get; init; }

    public int DebounceSeconds { get; init; } = 2;

    public int BackoffMaxSeconds { get; init; } = 30;
}
