using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests;

[Collection("ProcessGlobals")]
public sealed class TestEnvironmentScopeTests
{
    [Fact]
    public void Scope_RedirectsTheAgentClientProfile()
    {
        using var scope = new TestEnvironmentScope();

        McpOnboardingEnvironment.Create(TestDirs.NewTempDir()).HomeDir.ShouldBe(scope.HomeDir);
    }

    [Fact]
    public void Scope_LeavesTheCentralStoreWhereTheAssemblyPutIt()
    {
        var ambient = Environment.GetEnvironmentVariable("SQUEEZR_HOME");

        using (new TestEnvironmentScope())
        {
            Environment.GetEnvironmentVariable("SQUEEZR_HOME").ShouldBe(ambient);
        }
    }

    [Fact]
    public void Scope_RestoresPreviousValuesOnDispose()
    {
        var previous = Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME");
        previous.ShouldNotBeNullOrWhiteSpace();

        using (new TestEnvironmentScope())
        {
            Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME").ShouldNotBe(previous);
        }

        Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME").ShouldBe(previous);
    }

    [Fact]
    public void Scope_RestoresAnUnsetVariableAsUnsetRatherThanEmpty()
    {
        var previous = Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME");
        Environment.SetEnvironmentVariable("SQUEEZR_CLIENT_HOME", null);
        try
        {
            using (new TestEnvironmentScope())
            {
                Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME").ShouldNotBeNullOrWhiteSpace();
            }

            Environment.GetEnvironmentVariable("SQUEEZR_CLIENT_HOME").ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQUEEZR_CLIENT_HOME", previous);
        }
    }
}
