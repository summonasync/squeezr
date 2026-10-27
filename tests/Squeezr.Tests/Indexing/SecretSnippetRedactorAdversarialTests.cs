using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

// The five-round adversarial set from review-098-01 kept as a standing regression wall: every form
// that once leaked, bypassed a branch, blew the stack or burned the timeout belt is pinned here
// with its canary, so a perf-oriented rewrite of the redactor can never trade a shape away.
public class SecretSnippetRedactorAdversarialTests
{
    public static IEnumerable<object[]> Forms()
    {
        yield return Form("json url credentials", "appsettings.json",
            """{ "MongoUri": "mongodb://svc:ADV01-CANARY@db.internal:27017" }""",
            masked: true, ["ADV01-CANARY"], ["\"MongoUri\": \"[redacted?]\""]);
        yield return Form("yaml flow url credentials", "docker-compose.yml",
            "vault: {uri: postgres://u:ADV02-CANARY@h/db}",
            masked: true, ["ADV02-CANARY"], ["uri: [redacted?]"]);
        yield return Form("env url credentials", ".env.example",
            "DATABASE_URL=postgres://u:ADV03-CANARY@h/db",
            masked: true, ["ADV03-CANARY"], ["DATABASE_URL=[redacted?]"]);
        yield return Form("xml url credentials", "myapp.config",
            "<endpoint>mysql://u:ADV04-CANARY@h</endpoint>",
            masked: true, ["ADV04-CANARY"], ["<endpoint>[redacted?]</endpoint>"]);
        yield return Form("url without credentials stays plain", "appsettings.json",
            """{ "Endpoint": "https://vault.internal:8200" }""",
            masked: false, [], ["https://vault.internal:8200"]);
        yield return Form("yaml flow map secret", "docker-compose.yml",
            "environment: {POSTGRES_PASSWORD: ADV05-CANARY, POSTGRES_PORT: 5432}",
            masked: true, ["ADV05-CANARY"], ["POSTGRES_PASSWORD: [redacted]", "POSTGRES_PORT: 5432"]);
        yield return Form("quoted flow value with comma", "docker-compose.yml",
            """environment: {POSTGRES_PASSWORD: "ADV06-CANARY,extra9"}""",
            masked: true, ["ADV06-CANARY", "extra9"], ["POSTGRES_PASSWORD: [redacted]"]);
        yield return Form("compact json secret and plain pair", "appsettings.json",
            """{ "ApiKey": "ADV07-CANARY", "Port": "9090" }""",
            masked: true, ["ADV07-CANARY"], ["\"ApiKey\": \"[redacted]\"", "\"Port\": \"9090\""]);
        yield return Form("nested struct under plain outer key", "appsettings.json",
            """{ "Db": {"Password": "ADV08-CANARY"}, "Name": "app" }""",
            masked: true, ["ADV08-CANARY"], ["\"Password\": \"[redacted]\"", "\"Name\": \"app\""]);
        yield return Form("non-family outer key struct", "appsettings.json",
            """{ "Data": {"Token": "ADV09-CANARY"} }""",
            masked: true, ["ADV09-CANARY"], ["\"Token\": \"[redacted]\""]);
        yield return Form("array under family key", "docker-compose.yml",
            "apikeys: [ADV10-A, ADV10-B]",
            masked: true, ["ADV10-A", "ADV10-B"], ["apikeys: [redacted]"]);
        yield return Form("plain array stays plain", "docker-compose.yml",
            "tags: [one, two]",
            masked: false, [], ["tags: [one, two]"]);
        yield return Form("xml key value attributes", "myapp.config",
            """<add key="ClientSecret" value="ADV11-CANARY" />""",
            masked: true, ["ADV11-CANARY"], ["key=\"ClientSecret\"", "value=\"[redacted]\""]);
        yield return Form("xml name attribute secret", "myapp.config",
            """<variable name="SmtpPassword" value="ADV12-CANARY" />""",
            masked: true, ["ADV12-CANARY"], ["value=\"[redacted]\""]);
        yield return Form("xml plain attributes stay plain", "myapp.config",
            """<variable name="LogDir" value="logs" />""",
            masked: false, [], ["name=\"LogDir\" value=\"logs\""]);
        yield return Form("xml element secret", "myapp.config",
            "<password>ADV13-CANARY</password>",
            masked: true, ["ADV13-CANARY"], ["<password>[redacted]</password>"]);
        yield return Form("env template secret and plain", ".env.example",
            "OPENAI_API_KEY=ADV14-CANARY\nLOG_LEVEL=debug",
            masked: true, ["ADV14-CANARY"], ["OPENAI_API_KEY=[redacted]", "LOG_LEVEL=debug"]);
        yield return Form("compose list env secret", "docker-compose.yml",
            "- POSTGRES_PASSWORD=ADV15-CANARY\n- POSTGRES_PORT=5432",
            masked: true, ["ADV15-CANARY"], ["POSTGRES_PASSWORD=[redacted]", "POSTGRES_PORT=5432"]);
        yield return Form("connection string embedded assignment", "appsettings.json",
            """{ "Default": "Server=x;Password=ADV16-CANARY" }""",
            masked: true, ["ADV16-CANARY"], ["\"Default\": \"[redacted?]\""]);
        yield return Form("private key marker", ".env.example",
            "IMPORT_KEY=\"-----BEGIN RSA PRIVATE KEY-----ADV17-CANARY\"",
            masked: true, ["ADV17-CANARY", "BEGIN RSA PRIVATE"], ["IMPORT_KEY=[redacted?]"]);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Redact_AdversarialForm_MasksEverySecretCarrierAndKeepsStructure(
        string name, string path, string text, bool masked, string[] canaries, string[] expected)
    {
        var redacted = SecretSnippetRedactor.Redact(path, text);

        redacted.Count(c => c == '\n').ShouldBe(text.Count(c => c == '\n'), $"form {name} moved a line boundary");
        foreach (var fragment in expected)
        {
            redacted.Contains(fragment, StringComparison.Ordinal)
                .ShouldBeTrue($"form {name} lost a kept fragment: {fragment}");
        }

        if (masked)
        {
            redacted.ShouldContain("[redacted");
        }
        else
        {
            redacted.ShouldBe(text, $"form {name} changed a plain line");
        }

        foreach (var canary in canaries)
        {
            redacted.Contains(canary, StringComparison.Ordinal)
                .ShouldBeFalse($"form {name} leaked a canary: {canary}");
        }
    }

    [Fact]
    public void Redact_SecretHiddenInsideHostileWordRun_IsStillMasked()
    {
        var payload = new string('a', 10_000) + "password=ADV18-CANARY" + new string('a', 10_000);
        var line = "{ \"Data\": \"" + payload + "\" }";

        var masked = SecretSnippetRedactor.Redact("appsettings.json", line);

        masked.Count(c => c == '\n').ShouldBe(0);
        masked.ShouldContain("[redacted?]");
        masked.ShouldNotContain("ADV18-CANARY");
    }

    [Fact]
    public void Redact_SecretBeyondTheDepthCap_FailsClosedWithoutCrashing()
    {
        var line = string.Concat(Enumerable.Repeat("{ \"Key\": ", 5_000))
                  + "\"ADV19-CANARY\""
                  + string.Concat(Enumerable.Repeat(" }", 5_000));

        var masked = SecretSnippetRedactor.Redact("appsettings.json", line);

        masked.Count(c => c == '\n').ShouldBe(0);
        masked.ShouldContain("[redacted?]");
        masked.ShouldNotContain("ADV19-CANARY");
    }

    private static object[] Form(
        string name, string path, string text, bool masked, string[] canaries, string[] expected) =>
        [name, path, text, masked, canaries, expected];
}
