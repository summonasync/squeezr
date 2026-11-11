namespace Squeezr.Core.Infra.Autostart;

// The (program, arguments) pair an entry runs, in argv shape: no cmd wrapper, no shell escaping.
// Not a substitute for the /TR token — that one carries the SQUEEZR_HOME wrapper and cmd escaping.
//
// The two directions read the arguments differently, and both tell the whole truth about a unit.
// Install hands the generator the program plus the payload tokens that come BEFORE the fixed
// 'watchd --autostart' tail: the generator owns that tail, so a pair carrying it is refused rather
// than doubled into a unit that execs the verb twice. WatchdUnitProgram reads the other way and
// reports everything the entry really carries after the program, verb and flag included — a reader
// that trimmed the tail would hide an entry whose verb somebody edited away.
public sealed record WatchdRunCommand(string Executable, IReadOnlyList<string> Arguments);
