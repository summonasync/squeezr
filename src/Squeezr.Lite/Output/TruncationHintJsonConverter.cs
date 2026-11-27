using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Lite.Output;

public sealed class TruncationHintJsonConverter : JsonConverter<TruncationHint>
{
    public override TruncationHint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A truncation hint is never read back from json.");

    public override void Write(Utf8JsonWriter writer, TruncationHint value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ForCli());
}
