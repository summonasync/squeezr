using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

public sealed record IndexingConfig
{
    public required IReadOnlyList<string> Ignore { get; init; }

    public required int MaxFileSizeKb { get; init; }

    public bool NoSemantic { get; init; }

    [JsonConverter(typeof(RankExcludeJsonConverter))]
    public IReadOnlyList<string> RankExclude { get; init; } = [];

    public IReadOnlyList<string> SecretGuardExtra { get; init; } = [];

    public bool RespectGitignore { get; init; } = true;
}
