using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing;

public class DocKindDetectorTests
{
    [Theory]
    [InlineData("csharp", DocKindDetector.Code)]
    [InlineData("javascript", DocKindDetector.Code)]
    [InlineData("typescript", DocKindDetector.Code)]
    [InlineData("tsx", DocKindDetector.Code)]
    [InlineData("python", DocKindDetector.Code)]
    [InlineData("sql", DocKindDetector.Code)]
    [InlineData("css", DocKindDetector.Code)]
    [InlineData("html", DocKindDetector.Code)]
    [InlineData("shell", DocKindDetector.Code)]
    [InlineData("markdown", DocKindDetector.Docs)]
    [InlineData("text", DocKindDetector.Docs)]
    [InlineData("json", DocKindDetector.Docs)]
    [InlineData("yaml", DocKindDetector.Docs)]
    [InlineData("toml", DocKindDetector.Docs)]
    [InlineData("xml", DocKindDetector.Docs)]
    [InlineData("unknown", DocKindDetector.Docs)]
    [InlineData("", DocKindDetector.Docs)]
    public void Detect_KnownLanguage_SplitsCodeFromDocs(string language, string expected)
    {
        DocKindDetector.Detect(language).ShouldBe(expected);
    }

    [Fact]
    public void Detect_StoredKind_MissesLegacyRowsThroughLanguage()
    {
        var legacy = new FileDocument { Language = "markdown", Kind = "" };
        var stamped = new FileDocument { Language = "text", Kind = DocKindDetector.Code };

        DocKindDetector.Detect(legacy).ShouldBe(DocKindDetector.Docs);
        DocKindDetector.Detect(stamped).ShouldBe(DocKindDetector.Code);
    }
}
