using Squeezr.Core.Indexing.Query;

namespace Squeezr.Core.Indexing.Navigation;

public sealed class TokenBudget
{
    public TokenBudget(int maxTokens)
    {
        MaxTokens = maxTokens;
    }

    public int MaxTokens { get; }

    public int Used { get; private set; }

    public bool Truncated { get; private set; }

    public int RefusedBodyTokens { get; private set; }

    public bool TryTake(string text, int? knownTokens = null, bool isBody = false)
    {
        var tokens = knownTokens ?? TokenCounter.Estimate(text);
        if (Used + tokens > MaxTokens)
        {
            Truncated = true;
            if (isBody)
            {
                RefusedBodyTokens += tokens;
            }

            return false;
        }

        Used += tokens;
        return true;
    }
}
