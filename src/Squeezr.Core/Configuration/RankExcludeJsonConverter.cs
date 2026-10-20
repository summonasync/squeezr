using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

// a shape-broken rankExclude (a bare string, numbers or objects inside the array) used to throw a
// JsonException that ConfigFile.Load turned into a fatal WorkspaceException — one typo in one key
// took down the whole config, embeddings included. The converter salvages what the author meant:
// a bare string becomes a one-element list, non-string array entries are skipped, anything else
// degrades to an empty list. Glob-shape errors stay on RankExclusion's warn+skip channel.
public sealed class RankExcludeJsonConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
            case JsonTokenType.None:
                return [];
            case JsonTokenType.String:
                return [reader.GetString()!];
            case JsonTokenType.StartArray:
                return ReadArray(ref reader);
            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var pattern in value)
        {
            writer.WriteStringValue(pattern);
        }

        writer.WriteEndArray();
    }

    private static List<string> ReadArray(ref Utf8JsonReader reader)
    {
        var patterns = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return patterns;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                patterns.Add(reader.GetString()!);
            }
            else
            {
                reader.Skip();
            }
        }

        return patterns;
    }
}
