using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeezr.Core.Serialization;

public static class SqueezrJson
{
    public static JsonSerializerOptions FileOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static JsonSerializerOptions OutputOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
