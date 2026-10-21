using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

[Collection("ProcessGlobals")]
public sealed class McpOnboardingEnvironmentTests : IDisposable
{
    private const string ClientHomeEnvironmentVariable = "SQUEEZR_CLIENT_HOME";
    private readonly string? _previous;

    public McpOnboardingEnvironmentTests() =>
        _previous = Environment.GetEnvironmentVariable(ClientHomeEnvironmentVariable);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ClientHomeEnvironmentVariable, _previous);

    [Fact]
    public void Create_WithClientHomeSet_UsesIt()
    {
        var home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(ClientHomeEnvironmentVariable, home);

        McpOnboardingEnvironment.Create(TestDirs.NewTempDir()).HomeDir.ShouldBe(home);
    }

    [Fact]
    public void Create_WithoutClientHome_FallsBackToTheUserProfile()
    {
        Environment.SetEnvironmentVariable(ClientHomeEnvironmentVariable, null);

        McpOnboardingEnvironment.Create(TestDirs.NewTempDir()).HomeDir
            .ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
    }

    [Fact]
    public void Create_WithWhitespaceClientHome_FallsBackInsteadOfProducingABlankHome()
    {
        Environment.SetEnvironmentVariable(ClientHomeEnvironmentVariable, "   ");

        McpOnboardingEnvironment.Create(TestDirs.NewTempDir()).HomeDir
            .ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
    }
}
