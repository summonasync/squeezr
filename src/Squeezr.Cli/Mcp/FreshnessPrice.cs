using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

// The price of a fragment is what it costs the caller on the wire, not what it costs the envelope: an answer
// travels as JSON text nested inside the MCP content string, where one quote character becomes six escaped
// bytes, so a fragment pays five per quote on top of its own bytes. The product's own byte estimator prices
// those bytes — the same ceil(len/4*1.1) that answers for a whole file — and fragments are priced one at a
// time and summed, which is the basis the review arms are measured on.
//
// One rule, four tools: a tool never counts its own freshness payload by hand.
public static class FreshnessPrice
{
    private const int QuotePenaltyBytes = 5;

    public static int JsonTokens<T>(T fragment) =>
        Tokens(JsonSerializer.Serialize(fragment, SqueezrJson.OutputOptions));

    public static int LineTokens(string line) => Tokens("\"" + line + "\"");

    public static int Tokens(string json) =>
        (int)TokenEstimate.WholeFile(json.Length + QuotePenaltyBytes * json.Count(c => c == '\"'));
}
