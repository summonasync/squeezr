using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

// Regression belt for the 098 pathology: a covered config line carrying a 20k+ word-run used to
// burn the full two-second RegexMatchTimeout belt (the scans resumed at every position of the run)
// before the line failed closed. The committed fixtures below reproduce that shape family; the
// threshold sits fourfold under the measured belt-fire cost, so a backtracking regression
// cannot slip through while the suite stays stable on loaded CI machines.
public class SecretSnippetRedactorBenchmarkTests
{
    private const int RunLength = 24_000;
    private const int ThresholdMs = 500;

    [Fact]
    public void Redact_BareWordRun_StaysFarUnderTheOldTimeoutBelt()
    {
        var line = new string('a', RunLength);

        Timed("bare word-run", line).ShouldBeLessThan(ThresholdMs);
    }

    [Fact]
    public void Redact_SpacedWordRun_StaysFarUnderTheOldTimeoutBelt()
    {
        var line = string.Join(' ', Enumerable.Repeat("settings", 3_400));

        Timed("spaced word-run", line).ShouldBeLessThan(ThresholdMs);
    }

    [Fact]
    public void Redact_QuotedWordRun_StaysFarUnderTheOldTimeoutBelt()
    {
        var line = "{ \"Data\": \"" + new string('a', 30_000) + "\" }";

        Timed("quoted word-run", line).ShouldBeLessThan(ThresholdMs);
    }

    [Fact]
    public void Redact_UrlPrefixWithoutCredentialsOnWordRun_StaysFarUnderTheOldTimeoutBelt()
    {
        var line = "see https://gw.internal/echo/" + new string('a', RunLength);

        Timed("url without credentials", line).ShouldBeLessThan(ThresholdMs);
    }

    [Fact]
    public void Redact_UrlWithCredentialsOnWordRun_MasksFastAndKeepsWorking()
    {
        var line = "{ \"Uri\": \"mongodb://svc:SC9-CANARY-URI-VALUE@" + new string('a', RunLength) + "\" }";

        string masked = null!;
        var elapsed = Timed("url with credentials", line, () => masked = SecretSnippetRedactor.Redact("appsettings.json", line));

        elapsed.ShouldBeLessThan(ThresholdMs);
        masked.ShouldContain("[redacted?]");
        masked.ShouldNotContain("SC9-CANARY-URI-VALUE");
    }

    private static long Timed(string name, string line, Action? capture = null)
    {
        SecretSnippetRedactor.Redact("appsettings.json", line);
        capture?.Invoke();

        var sw = Stopwatch.StartNew();
        SecretSnippetRedactor.Redact("appsettings.json", line);
        sw.Stop();

        return sw.ElapsedMilliseconds;
    }
}
