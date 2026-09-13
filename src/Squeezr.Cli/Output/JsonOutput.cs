using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Output;

public static class JsonOutput
{
    public static void Write<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, SqueezrJson.OutputOptions));

    public static void WriteLine<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, SqueezrJson.OutputOptions));
        Console.Out.Flush();
    }

    public static void WriteError(string code, string message) =>
        Console.Error.WriteLine(
            JsonSerializer.Serialize(new { error = new JsonErrorBody(code, message) }, SqueezrJson.OutputOptions));
}
