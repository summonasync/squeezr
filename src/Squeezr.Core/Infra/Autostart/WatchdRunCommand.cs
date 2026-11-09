namespace Squeezr.Core.Infra.Autostart;

// ArgumentList-shaped form of the command the unit files carry: no cmd wrapper, no shell escaping.
// Not a substitute for the /TR token — that one carries the SQUEEZR_HOME wrapper and cmd escaping.
public sealed record WatchdRunCommand(string Executable, IReadOnlyList<string> Arguments);
