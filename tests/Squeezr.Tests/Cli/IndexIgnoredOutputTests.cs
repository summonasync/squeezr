using System.Reflection;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Cli;

public class IndexIgnoredOutputTests
{
    [Fact]
    public void Completed_WithIgnored_SerializesIgnoredField()
    {
        var writer = new StringWriter();
        var result = new IndexResult(
            [new RepoIndexResult("demo", 1, 0, 0, 0, 5, 3)],
            DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 42, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        var repo = doc.RootElement.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.GetProperty("ignored").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void Completed_WithZeroIgnored_StillSerializesField()
    {
        var writer = new StringWriter();
        var result = new IndexResult(
            [new RepoIndexResult("demo", 1, 0, 0, 0, 5, 0)],
            DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 42, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        var repo = doc.RootElement.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.GetProperty("ignored").GetInt32().ShouldBe(0);
        repo.TryGetProperty("ignoredBySource", out _).ShouldBeFalse();
    }

    [Fact]
    public void Completed_WithBreakdown_WritesSourceKeysSharedWithStatusSurface()
    {
        var writer = new StringWriter();
        var result = new IndexResult(
            [new RepoIndexResult("demo", 1, 0, 0, 0, 5, 40,
                new Dictionary<string, int> { ["gitignore"] = 36, ["defaults"] = 4 })],
            DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 42, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        var repo = doc.RootElement.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        var breakdown = repo.GetProperty("ignoredBySource");

        breakdown.GetProperty("gitignore").GetInt32().ShouldBe(36);
        breakdown.GetProperty("defaults").GetInt32().ShouldBe(4);
        breakdown.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["defaults", "gitignore"]);
    }

    [Fact]
    public void RepoIndexResult_CarriesNoEnumKeyedCollections()
    {
        var offenders = typeof(RepoIndexResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => ReferencesIgnoreSource(property.PropertyType))
            .Select(property => property.Name)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void IgnoreSourceKey_RoundTripsEveryMember()
    {
        foreach (var source in Enum.GetValues<IgnoreSource>())
        {
            var key = IgnoreSourceKey.Of(source);
            IgnoreSourceKey.All.ShouldContain(key);
            key.ShouldBe(source.ToString().ToLowerInvariant());
        }
    }

    private static bool ReferencesIgnoreSource(Type type) =>
        type == typeof(IgnoreSource)
        || (type.IsGenericType && type.GetGenericArguments().Any(ReferencesIgnoreSource));
}
