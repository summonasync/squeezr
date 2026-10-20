using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Configuration;

public class WatchConfigTests
{
    [Fact]
    public void Load_ConfigWithoutWatchSection_UsesDefaults()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(
            SqueezrPaths.ConfigPath(root),
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        AssertDefaults(ConfigFile.Load(root));
    }

    [Fact]
    public void Load_ExplicitWatchSection_ReadsExactValues()
    {
        var config = LoadWatchSection(
            """{ "enabled": true, "debounceSeconds": 5, "backoffMaxSeconds": 90, "embedPending": false, "embedBudgetPerPass": 7 }""");

        config.Watch.Enabled.ShouldBeTrue();
        config.Watch.DebounceSeconds.ShouldBe(5);
        config.Watch.BackoffMaxSeconds.ShouldBe(90);
        config.Watch.EmbedPending.ShouldBeFalse();
        config.Watch.EmbedBudgetPerPass.ShouldBe(7);
    }

    [Fact]
    public void Load_NullWatchSection_UsesDefaults()
    {
        AssertDefaults(LoadWatchSection("null"));
    }

    [Fact]
    public void Load_WatchSectionNotAnObject_UsesDefaults()
    {
        AssertDefaults(LoadWatchSection("42"));
    }

    [Fact]
    public void Load_StringInsteadOfNumber_KeepsFieldDefaults()
    {
        var config = LoadWatchSection(
            """{ "enabled": true, "debounceSeconds": "fast", "backoffMaxSeconds": "soon" }""");

        config.Watch.Enabled.ShouldBeTrue();
        config.Watch.DebounceSeconds.ShouldBe(2);
        config.Watch.BackoffMaxSeconds.ShouldBe(30);
    }

    [Fact]
    public void Load_EnabledAsString_FallsBackToDefault()
    {
        var config = LoadWatchSection("""{ "enabled": "yes", "debounceSeconds": 5 }""");

        config.Watch.Enabled.ShouldBeFalse();
        config.Watch.DebounceSeconds.ShouldBe(5);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void Load_NonPositiveDebounceSeconds_FallsBackToDefault(int value)
    {
        var config = LoadWatchSection($$"""{ "debounceSeconds": {{value}} }""");

        config.Watch.DebounceSeconds.ShouldBe(2);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    public void Load_NonPositiveBackoffMaxSeconds_FallsBackToDefault(int value)
    {
        var config = LoadWatchSection($$"""{ "backoffMaxSeconds": {{value}} }""");

        config.Watch.BackoffMaxSeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Load_NonPositiveEmbedBudgetPerPass_FallsBackToDefault(int value)
    {
        var config = LoadWatchSection($$"""{ "embedBudgetPerPass": {{value}} }""");

        config.Watch.EmbedBudgetPerPass.ShouldBe(WatchConfig.DefaultEmbedBudgetPerPass);
    }

    [Fact]
    public void Load_EmbedPendingAsString_FallsBackToDefault()
    {
        var config = LoadWatchSection("""{ "embedPending": "off", "embedBudgetPerPass": 9 }""");

        config.Watch.EmbedPending.ShouldBeTrue();
        config.Watch.EmbedBudgetPerPass.ShouldBe(9);
    }

    [Fact]
    public void Load_UnknownNestedPropertyInWatchSection_SkipsItAndKeepsKnownFields()
    {
        var config = LoadWatchSection(
            """{ "futureOption": { "nested": [1, 2] }, "debounceSeconds": 5 }""");

        config.Watch.DebounceSeconds.ShouldBe(5);
        config.Watch.Enabled.ShouldBeFalse();
        config.Watch.BackoffMaxSeconds.ShouldBe(30);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsWatchSection()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Watch = new WatchConfig
            {
                Enabled = true,
                DebounceSeconds = 7,
                BackoffMaxSeconds = 120,
                EmbedPending = false,
                EmbedBudgetPerPass = 32
            }
        };

        ConfigFile.Save(root, config);
        var loaded = ConfigFile.Load(root);

        loaded.Watch.Enabled.ShouldBeTrue();
        loaded.Watch.DebounceSeconds.ShouldBe(7);
        loaded.Watch.BackoffMaxSeconds.ShouldBe(120);
        loaded.Watch.EmbedPending.ShouldBeFalse();
        loaded.Watch.EmbedBudgetPerPass.ShouldBe(32);
    }

    private static void AssertDefaults(SqueezrConfig config)
    {
        config.Watch.Enabled.ShouldBeFalse();
        config.Watch.DebounceSeconds.ShouldBe(2);
        config.Watch.BackoffMaxSeconds.ShouldBe(30);
        config.Watch.EmbedPending.ShouldBeTrue();
        config.Watch.EmbedBudgetPerPass.ShouldBe(WatchConfig.DefaultEmbedBudgetPerPass);
    }

    private static SqueezrConfig LoadWatchSection(string watchValue)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(
            SqueezrPaths.ConfigPath(root),
            $$"""
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "qdrant": { "endpoint": "http://localhost:6334" },
              "watch": {{watchValue}}
            }
            """);
        return ConfigFile.Load(root);
    }
}
