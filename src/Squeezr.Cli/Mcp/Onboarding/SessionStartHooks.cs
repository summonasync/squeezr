using System.Text.Json;

namespace Squeezr.Cli.Mcp.Onboarding;

// The SessionStart hook's arm / strip / measure door, kept out of McpOnboardingService because arming it is a
// separate opt-in act: install registers the server and arms the pre-read hook whether or not this block goes
// in, so the pass that writes it has to be callable and reportable on its own.
//
// It writes only through ManagedHooksWriter, so the merge rules, the backup and the byte-identity no-op are the
// ones the pre-read hook already lives under — and a block written here is what remove takes back out without
// being asked, because a hook that outlives the registration that armed it is litter with a command in it.
public sealed class SessionStartHooks
{
    private static readonly JsonSerializerOptions SnippetOptions = new() { WriteIndented = true };

    public async Task<SessionStartHookWrite?> ArmAsync(
        McpClientDescriptor client,
        McpScope scope,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (HooksBlock.ForSessionStart(client, environment.CommandPath) is not { } block ||
            McpClientPaths.HooksPath(client, environment, scope) is not { } hooksPath)
        {
            return null;
        }

        await ManagedHooksWriter.UpsertAsync(hooksPath, block, cancellationToken);
        return new SessionStartHookWrite(hooksPath, block.ToJson().ToJsonString(SnippetOptions));
    }

    public async Task<bool> StripAsync(
        McpClientDescriptor client,
        McpScope scope,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return HooksBlock.ForSessionStart(client, environment.CommandPath) is { } block &&
            McpClientPaths.HooksPath(client, environment, scope) is { } hooksPath &&
            await ManagedHooksWriter.RemoveAsync(hooksPath, block, cancellationToken);
    }

    public async Task<string> StatusAsync(
        McpClientDescriptor client,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (HooksBlock.ForSessionStart(client, environment.CommandPath) is not { } block ||
            McpClientPaths.HooksPath(client, environment, McpScope.User) is not { } userHooksPath)
        {
            return HooksBlock.LayoutUnverifiedReason;
        }

        if (await ManagedHooksWriter.IsArmedAsync(userHooksPath, block, cancellationToken))
        {
            return HooksBlock.ArmedState;
        }

        var projectHooksPath = McpClientPaths.HooksPath(client, environment, McpScope.Project);
        return projectHooksPath is not null &&
            await ManagedHooksWriter.IsArmedAsync(projectHooksPath, block, cancellationToken)
            ? HooksBlock.ProjectArmedState
            : HooksBlock.NotArmedState;
    }
}
