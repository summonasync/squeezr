using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

// Plan R2: TUI and scripts consume today's ServicesUpReport JSON - the per-service fields land
// additively at the end, and a payload written before 069 keeps deserializing.
public class ServicesUpReportTests
{
    [Fact]
    public void Deserialize_LegacyPayloadWithoutServices_AllDefaultsAreEmpty()
    {
        const string legacy =
            """
            {
              "composeFile": "/home/x/infra/docker-compose.yml",
              "project": "squeezr",
              "rocm": false,
              "qdrantEndpoint": "http://localhost:6334",
              "qdrantRestEndpoint": "http://localhost:6333",
              "ollamaEndpoint": "http://localhost:11434",
              "composeVersion": "v2.39.0"
            }
            """;

        var report = JsonSerializer.Deserialize<ServicesUpReport>(legacy, SqueezrJson.OutputOptions);

        report.ShouldNotBeNull();
        report!.ComposeFile.ShouldBe("/home/x/infra/docker-compose.yml");
        report.ComposeVersion.ShouldBe("v2.39.0");
        report.Services.ShouldBeNull();
    }

    [Fact]
    public void Serialize_WithServices_ExistingFieldOrderUntouchedAndServicesLast()
    {
        var report = new ServicesUpReport(
            "/home/x/infra/docker-compose.yml",
            "squeezr",
            true,
            "http://localhost:6334",
            "http://localhost:6333",
            "http://localhost:11434",
            "v2.39.0",
            [new ServicesUpServiceReport("ollama", "external-adopted", "bobai-ollama-1", "adopted external Ollama")]);

        var json = JsonSerializer.Serialize(report, SqueezrJson.OutputOptions);

        var composeFileAt = json.IndexOf("\"composeFile\":", StringComparison.Ordinal);
        var composeVersionAt = json.IndexOf("\"composeVersion\":", StringComparison.Ordinal);
        var servicesAt = json.IndexOf("\"services\":", StringComparison.Ordinal);
        composeFileAt.ShouldBeLessThan(composeVersionAt);
        composeVersionAt.ShouldBeLessThan(servicesAt);
        json.ShouldContain("\"external-adopted\"");
    }
}
