using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite;

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

    public static string LaunchDirectory() => Directory.GetCurrentDirectory();

    public static string RequireWorkspaceRoot()
    {
        var resolution = ResolveCurrentDirectory();
        if (!resolution.IsWorkspace)
        {
            throw new WorkspaceException(
                LiteRemedy.WorkspaceNotInitialized,
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
            when (ex is not WorkspaceStoreRemovalException and not WorkspaceStoreRemovalUnverifiedException)
        {
            throw RegistryBusy(ex);
        }
    }

    private static WorkspaceException RegistryBusy(IOException cause) =>
        new($"{RegistryBusyMessage} ({cause.GetType().Name}: {cause.Message})",
            WorkspaceException.RegistryBusyCode,
            cause);
}
