using Shouldly;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Indexing;

public class SecretSnippetCoverageTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("src/Api/appsettings.Development.json")]
    [InlineData("Config/appsettings.staging.json")]
    [InlineData(".env.example")]
    [InlineData("deploy/.env.template")]
    [InlineData(".env.sample")]
    [InlineData("docker-compose.yml")]
    [InlineData("ops/docker-compose.prod.yaml")]
    [InlineData("web.config")]
    [InlineData("config/myapp.config")]
    public void IsCovered_BuiltInConfigShapes_AnswerTrueWithZeroConfig(string relativePath) =>
        SecretSnippetCoverage.IsCovered(relativePath).ShouldBeTrue();

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("deploy/keys/monkey.pem")]
    [InlineData("config/secrets.json")]
    [InlineData("src/App.cs")]
    [InlineData("package.json")]
    [InlineData("compose.yaml")]
    [InlineData("notes/settings.txt")]
    public void IsCovered_TrueSecretFilesAndOrdinaryCode_AnswerFalse(string relativePath) =>
        SecretSnippetCoverage.IsCovered(relativePath).ShouldBeFalse();
}
