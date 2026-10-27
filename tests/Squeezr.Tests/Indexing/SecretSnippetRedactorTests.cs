using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

public class SecretSnippetRedactorTests
{
    private const string AppSettings = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost;Database=orders;Password=SC1-CANARY-CONN-VALUE"
          },
          "ApiKey": "SC2-CANARY-KEY-VALUE",
          "Logging": {
            "LogLevel": {
              "Default": "Information"
            }
          },
          "Vault": "address=https://vault.internal;token=SC3-CANARY-VAULT-VALUE",
          "AllowedHosts": "*",
          "Port": "8080"
        }
        """;

    [Fact]
    public void Redact_CertainSecretKeys_LoseTheirValueToTheCertainMarker()
    {
        var masked = SecretSnippetRedactor.Redact("appsettings.json", AppSettings);

        masked.ShouldContain("\"ApiKey\": \"[redacted]\"");
        masked.ShouldNotContain("SC2-CANARY-KEY-VALUE");
    }

    [Fact]
    public void Redact_ValueCarryingASecretAssignment_LosesItsValueToTheDoubtMarker()
    {
        var masked = SecretSnippetRedactor.Redact("appsettings.json", AppSettings);

        masked.ShouldContain("\"Default\": \"[redacted?]\"");
        masked.ShouldContain("\"Vault\": \"[redacted?]\"");
        masked.ShouldNotContain("SC1-CANARY-CONN-VALUE");
        masked.ShouldNotContain("SC3-CANARY-VAULT-VALUE");
    }

    [Fact]
    public void Redact_KeepsStructureAndPlainValuesSearchable()
    {
        var masked = SecretSnippetRedactor.Redact("appsettings.json", AppSettings);

        masked.ShouldContain("\"ConnectionStrings\": {");
        masked.ShouldContain("\"Default\": \"Information\"");
        masked.ShouldContain("\"Port\": \"8080\"");
        masked.ShouldContain("\"AllowedHosts\": \"*\"");
    }

    [Fact]
    public void Redact_MovesNoLineBoundary()
    {
        var masked = SecretSnippetRedactor.Redact("appsettings.json", AppSettings);

        masked.Count(c => c == '\n').ShouldBe(AppSettings.Count(c => c == '\n'));
    }

    [Fact]
    public void Redact_BothMarkersLandOnTheirOwnLines_AndStayDistinct()
    {
        var masked = SecretSnippetRedactor.Redact("appsettings.json", AppSettings);

        Occurrences(masked, "[redacted?]").ShouldBe(2);
        Occurrences(masked, "[redacted]").ShouldBe(1);
    }

    private static int Occurrences(string text, string needle) =>
        (text.Length - text.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    [Fact]
    public void Redact_EnvTemplate_MasksSecretValuesAndKeepsPlainOnes()
    {
        var masked = SecretSnippetRedactor.Redact(
            ".env.example", "OPENAI_API_KEY=sk-live-canary-1\nRETENTION_DAYS=30\nLOG_LEVEL=debug\n");

        masked.ShouldContain("OPENAI_API_KEY=[redacted]");
        masked.ShouldContain("RETENTION_DAYS=30");
        masked.ShouldContain("LOG_LEVEL=debug");
        masked.ShouldNotContain("sk-live-canary-1");
    }

    [Fact]
    public void Redact_DockerCompose_MasksSecretEnvironmentEntries()
    {
        const string compose = """
            services:
              db:
                image: postgres:16
                environment:
                  - POSTGRES_PASSWORD=hunter2-canary
                  - POSTGRES_PORT=5432
            """;

        var masked = SecretSnippetRedactor.Redact("docker-compose.yml", compose);

        masked.ShouldContain("- POSTGRES_PASSWORD=[redacted]");
        masked.ShouldContain("- POSTGRES_PORT=5432");
        masked.ShouldContain("image: postgres:16");
        masked.ShouldNotContain("hunter2-canary");
    }

    [Fact]
    public void Redact_XmlConfig_MasksKeyValuePairsAndNamedAttributes()
    {
        const string appConfig = """
            <configuration>
              <appSettings>
                <add key="ClientSecret" value="cs-canary-1" />
                <add key="Timeout" value="30" />
              </appSettings>
              <password>pw-canary-1</password>
              <identity password="pw-canary-2" />
            </configuration>
            """;

        var masked = SecretSnippetRedactor.Redact("myapp.config", appConfig);

        masked.ShouldContain("<add key=\"ClientSecret\" value=\"[redacted]\" />");
        masked.ShouldContain("<add key=\"Timeout\" value=\"30\" />");
        masked.ShouldContain("<password>[redacted]</password>");
        masked.ShouldContain("<identity password=\"[redacted]\" />");
        masked.ShouldNotContain("cs-canary-1");
        masked.ShouldNotContain("pw-canary-1");
        masked.ShouldNotContain("pw-canary-2");
    }

    [Fact]
    public void Redact_UncoveredPath_ReturnsTheTextUnchanged()
    {
        var masked = SecretSnippetRedactor.Redact("src/App.cs", AppSettings);

        masked.ShouldBe(AppSettings);
    }

    [Fact]
    public void Redact_CompactSingleLineJson_MasksEachSecretPairAndKeepsTheRest()
    {
        const string compact = """{ "ApiKey": "sc-canary-1", "Port": "9090", "Vault": "address=x;token=sc-canary-2" }""";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", compact);

        masked.ShouldContain("\"ApiKey\": \"[redacted]\"");
        masked.ShouldContain("\"Port\": \"9090\"");
        masked.ShouldContain("\"Vault\": \"[redacted?]\"");
        masked.ShouldNotContain("sc-canary-1");
        masked.ShouldNotContain("sc-canary-2");
    }

    [Fact]
    public void Redact_UrlWithEmbeddedCredentials_LosesItsValueToTheDoubtMarker()
    {
        const string appSettings = """{ "MongoUri": "mongodb://svc:uri-canary-1@db.internal:27017", "Endpoint": "https://vault.internal:8200" }""";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", appSettings);

        masked.ShouldContain("\"MongoUri\": \"[redacted?]\"");
        masked.ShouldContain("\"Endpoint\": \"https://vault.internal:8200\"");
        masked.ShouldNotContain("uri-canary-1");
    }

    [Fact]
    public void Redact_YamlFlowMap_MasksSecretEntriesInsideAndKeepsPlainOnes()
    {
        const string compose = """
            services:
              db:
                image: postgres:16
                environment: {POSTGRES_PASSWORD: flow-canary-1, POSTGRES_PORT: 5432}
            """;

        var masked = SecretSnippetRedactor.Redact("docker-compose.yml", compose);

        masked.ShouldContain("environment: {POSTGRES_PASSWORD: [redacted], POSTGRES_PORT: 5432}");
        masked.ShouldNotContain("flow-canary-1");
    }

    [Fact]
    public void Redact_XmlNamedValuePairs_MasksSecretHoldingNames()
    {
        const string appConfig = """
            <configuration>
              <variable name="SmtpPassword" value="name-canary-1" />
              <variable name="LogDir" value="logs" />
            </configuration>
            """;

        var masked = SecretSnippetRedactor.Redact("myapp.config", appConfig);

        masked.ShouldContain("<variable name=\"SmtpPassword\" value=\"[redacted]\" />");
        masked.ShouldContain("<variable name=\"LogDir\" value=\"logs\" />");
        masked.ShouldNotContain("name-canary-1");
    }

    [Fact]
    public void Redact_QuotedFlowValueWithComma_MasksTheWholeValue()
    {
        const string compose = "services:\n  db:\n    environment: {POSTGRES_PASSWORD: \"hunter2,extra9\"}\n";

        var masked = SecretSnippetRedactor.Redact("docker-compose.yml", compose);

        masked.ShouldContain("environment: {POSTGRES_PASSWORD: [redacted]}");
        masked.ShouldNotContain("hunter2");
        masked.ShouldNotContain("extra9");
    }

    [Fact]
    public void Redact_StructuralValueUnderFamilyKey_MasksTheWholeValue()
    {
        const string compose = "vault:\n  apikeys: [sk-abc-1, sk-def-2]\n  retries: 3\n";

        var masked = SecretSnippetRedactor.Redact("docker-compose.yml", compose);

        masked.ShouldContain("apikeys: [redacted]");
        masked.ShouldContain("retries: 3");
        masked.ShouldNotContain("sk-abc-1");
        masked.ShouldNotContain("sk-def-2");
    }

    [Fact]
    public void Redact_CompactJsonArrayUnderFamilyKey_MasksTheWholeArray()
    {
        var masked = SecretSnippetRedactor.Redact(
            "appsettings.json", """{ "Secrets": ["aaa-1", "bbb-2"], "Tags": ["one"] }""");

        masked.ShouldContain("\"Secrets\": [redacted]");
        masked.ShouldContain("\"Tags\": [\"one\"]");
        masked.ShouldNotContain("aaa-1");
        masked.ShouldNotContain("bbb-2");
    }

    [Fact]
    public void Redact_HostileLongWordRun_CompletesWithoutFailingTheLine()
    {
        var line = "{ \"Data\": \"" + new string('a', 30_000) + "\" }";

        string masked = null!;
        Should.NotThrow(() => masked = SecretSnippetRedactor.Redact("appsettings.json", line));
        masked.Count(c => c == '\n').ShouldBe(0);
    }

    [Fact]
    public void Redact_CompactNestedObjectUnderPlainKey_MasksInnerSecretPairs()
    {
        const string compact = """{ "Db": {"Password": "pw-inner-1"}, "Name": "app" }""";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", compact);

        masked.ShouldContain("\"Password\": \"[redacted]\"");
        masked.ShouldContain("\"Name\": \"app\"");
        masked.ShouldNotContain("pw-inner-1");
    }

    [Fact]
    public void Redact_CompactNestedObjectWithCredentialUrl_MasksTheInnerValue()
    {
        const string compact = """{ "Data": {"Uri": "mongodb://u:SuperSecret7@h"}, "Id": "7" }""";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", compact);

        masked.ShouldContain("\"Uri\": \"[redacted?]\"");
        masked.ShouldContain("\"Id\": \"7\"");
        masked.ShouldNotContain("SuperSecret7");
    }

    [Fact]
    public void Redact_DeeplyNestedCompactJson_FailsClosedWithoutCrashing()
    {
        var line = string.Concat(Enumerable.Repeat("{ \"Key\": ", 5_000))
                  + "1"
                  + string.Concat(Enumerable.Repeat(" }", 5_000));

        string masked = null!;
        Should.NotThrow(() => masked = SecretSnippetRedactor.Redact("appsettings.json", line));
        masked.Count(c => c == '\n').ShouldBe(0);
        masked.ShouldContain("[redacted?]");
    }

    [Fact]
    public void Redact_EmptyAndStructuralValues_StayUntouched()
    {
        const string shaped = "{\n  \"ConnectionStrings\": {\n    \"Default\": \"\"\n  }\n}";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", shaped);

        masked.ShouldContain("\"ConnectionStrings\": {");
        masked.ShouldContain("\"Default\": \"\"");
    }
}
