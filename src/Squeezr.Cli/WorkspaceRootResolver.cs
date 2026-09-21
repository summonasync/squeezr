using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli;

public static class WorkspaceRootResolver
{
    private const string RegistryBusyMessage = "Workspace registry is busy; retry in a moment.";

    public static WorkspaceResolution Resolve(string startDirectory)
    {
        WorkspaceResolution resolution;
        try
        {
            resolution = WorkspaceStore.Resolve(startDirectory);
        }
        catch (IOException ex)
        {
            throw RegistryBusy(ex);
        }

        if (resolution.MigratedFromLegacy)
        {
            Console.Error.WriteLine($"note: workspace migrated to the central store: {resolution.StoreDir}");
        }

        return resolution;
    }

    public static WorkspaceResolution ResolveCurrentDirectory() =>
        Resolve(Directory.GetCurrentDirectory());

    // Commands never call Directory.GetCurrentDirectory directly; this keeps the
    // process working directory as the single resolution entry point.
    public static string LaunchDirectory() => Directory.GetCurrentDirectory();

    public static string RequireWorkspaceRoot()
    {
        var resolution = ResolveCurrentDirectory();
        if (!resolution.IsWorkspace)
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);
        }

        return resolution.RootPath;
    }

    public static T GuardRegistryIo<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (IOException ex)
        {
            throw RegistryBusy(ex);
        }
    }

    // Every IOException on this path still maps to the busy contract - narrowing it
    // by exception type breaks under concurrent access. What changes is visibility:
    // the cause travels in the message, not only as InnerException, so a hard IO
    // defect is no longer indistinguishable from transient registry contention.
    private static WorkspaceException RegistryBusy(IOException cause) =>
        new($"{RegistryBusyMessage} ({cause.GetType().Name}: {cause.Message})",
            WorkspaceException.RegistryBusyCode,
            cause);
}
