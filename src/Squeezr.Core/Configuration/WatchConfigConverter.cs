using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

public sealed class WatchConfigConverter : JsonConverter<WatchConfig>
{
    public override bool HandleNull => true;

    public override WatchConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new WatchConfig();
        }

        var enabledName = PropertyName(options, nameof(WatchConfig.Enabled));
        var debounceName = PropertyName(options, nameof(WatchConfig.DebounceSeconds));
        var backoffName = PropertyName(options, nameof(WatchConfig.BackoffMaxSeconds));
        var embedPendingName = PropertyName(options, nameof(WatchConfig.EmbedPending));
        var embedBudgetName = PropertyName(options, nameof(WatchConfig.EmbedBudgetPerPass));

        var config = new WatchConfig();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == enabledName && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                config = config with { Enabled = reader.GetBoolean() };
            else if (name == debounceName && TryReadPositiveInt(reader, out var debounceSeconds))
                config = config with { DebounceSeconds = debounceSeconds };
            else if (name == backoffName && TryReadPositiveInt(reader, out var backoffMaxSeconds))
                config = config with { BackoffMaxSeconds = backoffMaxSeconds };
            else if (name == embedPendingName && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                config = config with { EmbedPending = reader.GetBoolean() };
            else if (name == embedBudgetName && TryReadPositiveInt(reader, out var embedBudgetPerPass))
                config = config with { EmbedBudgetPerPass = embedBudgetPerPass };
            else
                reader.Skip();
        }

        return config;
    }

    public override void Write(Utf8JsonWriter writer, WatchConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean(PropertyName(options, nameof(WatchConfig.Enabled)), value.Enabled);
        writer.WriteNumber(
            PropertyName(options, nameof(WatchConfig.DebounceSeconds)), value.DebounceSeconds);
        writer.WriteNumber(
            PropertyName(options, nameof(WatchConfig.BackoffMaxSeconds)), value.BackoffMaxSeconds);
        writer.WriteBoolean(PropertyName(options, nameof(WatchConfig.EmbedPending)), value.EmbedPending);
        writer.WriteNumber(
            PropertyName(options, nameof(WatchConfig.EmbedBudgetPerPass)), value.EmbedBudgetPerPass);
        writer.WriteEndObject();
    }

    private static bool TryReadPositiveInt(Utf8JsonReader reader, out int value)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value) && value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string PropertyName(JsonSerializerOptions options, string property) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}
