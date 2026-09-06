namespace Squeezr.Core.Configuration;

public sealed record OpenAiCompatibleOptions
{
    public required string Endpoint { get; init; }

    public required string ApiKeyEnv { get; init; }

    public required string Model { get; init; }

    public required int Dimensions { get; init; }
}
