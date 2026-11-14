namespace Squeezr.Tests;

/// <summary>
/// Points the agent-client profile at a throwaway directory, so a test that installs MCP
/// entries at user scope cannot reach the developer's real ~/.claude.json. The central
/// store is NOT redirected here - SqueezrTestEnvironment already isolates it for the whole
/// assembly, and moving it again would hide a workspace the test set up before entering
/// the scope. USERPROFILE is left alone: on Windows GetFolderPath(UserProfile) reads the
/// shell known-folder API rather than the variable, so redirecting it buys nothing while it
/// does steer NuGet and dotnet. Environment variables are process-global - only use this
/// inside a collection with DisableParallelization.
/// </summary>
public sealed class TestEnvironmentScope : IDisposable
{
    private const string HomeVariable = "HOME";
    private const string ClientHomeVariable = "SQUEEZR_CLIENT_HOME";

    private readonly (string Name, string? Value)[] _previous;

    public TestEnvironmentScope(string? homeDir = null)
    {
        _previous =
        [
            (HomeVariable, Environment.GetEnvironmentVariable(HomeVariable)),
            (ClientHomeVariable, Environment.GetEnvironmentVariable(ClientHomeVariable)),
        ];

        // A pinned home is for the doors that are supposed to remember what they wrote: install, then remove,
        // then doctor only say something about each other if all three see the same profile directory.
        HomeDir = homeDir ?? TestDirs.NewTempDir();

        Environment.SetEnvironmentVariable(HomeVariable, HomeDir);
        Environment.SetEnvironmentVariable(ClientHomeVariable, HomeDir);
    }

    public string HomeDir { get; }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            // A null value removes the variable, which is what restoring "was unset" means.
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
