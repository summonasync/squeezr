using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Commands;

// The hook reads the disk, and the disk knows nothing about the guard: a big protected file answered with
// advice while a missing one answered with silence, which is an existence oracle sitting outside the index
// plane. The harness hands over whatever the caller typed, and the ledger scrub and the stats ledger decide that
// same shape through the manifest fold — so the hook asks it too, rather than keeping its own idea of which
// root a path is written against.
internal static class HookSecretPaths
{
    internal static bool IsGuarded(string fullPath, WorkspaceRegistryEntry workspace) =>
        GuardedPathForms
            .Load(workspace.RootPath, SecretGuardReader.Load(workspace.RootPath))
            .NamesAProtectedPath(fullPath);
}
