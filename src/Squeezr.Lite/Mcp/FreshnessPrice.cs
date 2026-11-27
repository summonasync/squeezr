using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Lite.Mcp;

public static class FreshnessPrice
{
    private const int QuotePenaltyBytes = 5;

    public static int JsonTokens<T>(T fragment) =>
        Tokens(JsonSerializer.Serialize(fragment, SqueezrJson.OutputOptions));

    public static int LineTokens(string line) => Tokens("\"" + line + "\"");

    public static int Tokens(string json) =>
        (int)TokenEstimate.WholeFile(json.Length + QuotePenaltyBytes * json.Count(c => c == '\"'));
}
