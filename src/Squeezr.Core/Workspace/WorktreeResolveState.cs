namespace Squeezr.Core.Workspace;

// The states `worktree resolve` answers with. The three first ones are exit-0 answers: the verb read the world
// and reported what it found, including 'no store maps to this path', because a door a session hook calls every
// start must not pay a failure code for an ordinary directory. A tree that is GONE is the refusal — the registry
// may still be claiming rows for a directory nothing can measure, which is a state only a human can close.
//
// Unregistered and NotAWorktree stay apart on purpose: the first says 'git knows this checkout, the registry
// does not' and its remedy is the worktree verb; the second says 'nothing knows this path' and its remedy is
// plain init. Folding them into one 'not found' sends the reader to the wrong door.
public enum WorktreeResolveState
{
    Resolved,
    Unregistered,
    NotAWorktree,
    Gone,
}
