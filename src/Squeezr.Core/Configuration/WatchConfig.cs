using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

[JsonConverter(typeof(WatchConfigConverter))]
public sealed record WatchConfig
{
    public const int DefaultEmbedBudgetPerPass = 256;

    public bool Enabled { get; init; }

    public int DebounceSeconds { get; init; } = 2;

    public int BackoffMaxSeconds { get; init; } = 30;

    public bool EmbedPending { get; init; } = true;

    public int EmbedBudgetPerPass { get; init; } = DefaultEmbedBudgetPerPass;
}
