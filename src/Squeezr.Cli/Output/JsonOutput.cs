using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Output;

public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = CliOptions();

    public static void Write<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Options));

    public static void WriteLine<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Options));
        Console.Out.Flush();
    }

    public static void WriteError(
        string code,
        string message,
        IReadOnlyList<string>? removedPaths = null,
        IReadOnlyList<string>? nextSteps = null) =>
        Console.Error.WriteLine(
            JsonSerializer.Serialize(new { error = new JsonErrorBody(code, message, removedPaths, nextSteps) }, Options));

    public static void WriteWarnings(IReadOnlyList<JsonWarningBody> warnings) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { warnings }, Options));

    private static JsonSerializerOptions CliOptions()
    {
        var options = new JsonSerializerOptions(SqueezrJson.OutputOptions);
        options.Converters.Add(new TruncationHintJsonConverter());
        return options;
    }
}
