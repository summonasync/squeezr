namespace Squeezr.Core.Configuration;

public sealed record OpenAiCompatibleOptions
{
    public string Endpoint { get; init; } = "https://api.openai.com/v1";

    public string ApiKeyEnv { get; init; } = "SQUEEZR_API_KEY";

    public string Model { get; init; } = "text-embedding-3-large";

    public int Dimensions { get; init; } = 1024;
}
