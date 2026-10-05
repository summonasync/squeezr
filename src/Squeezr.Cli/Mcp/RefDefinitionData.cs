using System.Text.Json.Serialization;

namespace Squeezr.Cli.Mcp;

public sealed record RefDefinitionData(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine);
