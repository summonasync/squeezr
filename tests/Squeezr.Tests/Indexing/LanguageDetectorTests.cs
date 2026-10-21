using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("Squeezr.slnx", "xml")]
    [InlineData("Directory.Build.props", "xml")]
    [InlineData("Directory.Packages.props", "xml")]
    [InlineData("Directory.Build.targets", "xml")]
    [InlineData("MyApp.csproj", "xml")]
    [InlineData("MyApp.csproj.user", "unknown")]
    [InlineData("Squeezr.slnx.bak", "unknown")]
    [InlineData("Squeezr.slnx~", "unknown")]
    [InlineData("notes.unknownext", "unknown")]
    public void Detect_ProjectLayoutDialects_IsTrackedAsNonSource(string path, string expected)
    {
        var language = LanguageDetector.Detect(path);

        language.ShouldBe(expected);
    }

    [Fact]
    public void ProjectLayoutExtensions_CompositionIsPinnedAbsolutely()
    {
        // shrinking the list is the exact drift the watch seam lost to; queue-pins derive from the same array
        WorkspaceIndexer.ProjectLayoutExtensions
            .ShouldBe([".csproj", ".sln", ".slnx", ".props", ".targets"], ignoreOrder: true);
    }

    [Fact]
    public void Detect_ProjectLayoutDialects_AreNeverSourceLanguages()
    {
        foreach (var extension in WorkspaceIndexer.ProjectLayoutExtensions)
        {
            var language = LanguageDetector.Detect("Solution" + extension);
            language.ShouldNotBe("unknown", $"'{extension}' is tracked content, not an unknown drop");
            language.ShouldNotBe("csharp");
        }
    }
}
