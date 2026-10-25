using System.Text.Json;

namespace Squeezr.Cli.RankEval;

public sealed record GoldenQuery(string Query, string? Repo, string? Prefix, string[] Expected, string Note)
{
    public static IReadOnlyList<GoldenQuery> Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"golden set not found: {path}");
        }

        List<GoldenQuery>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<GoldenQuery>>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"golden set is not valid JSON: {path} ({ex.Message})");
        }

        if (entries is not { Count: > 0 })
        {
            throw new InvalidDataException($"golden set contains no queries: {path}");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(entries[i].Query) || entries[i].Expected is not { Length: > 0 })
            {
                throw new InvalidDataException(
                    $"golden set entry #{i + 1}: query and a non-empty expected[] are required ({path})");
            }
        }

        return entries;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
