using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Output;

public static class JsonOutput
{
    public static void Write<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, SqueezrJson.OutputOptions));
}
