namespace Squeezr.Core.Infra.Autostart;

// One answer shape for every dimension of an autostart probe, because the three states a diagnostic
// can actually be in are the same three everywhere: the platform said yes, the platform said no, or
// the platform did not answer. "No answer" splits in two of its own: the tool would not talk
// (Unknown) versus this platform has no such question (Unsupported) — a broken probe and a missing
// feature read differently to whoever is fixing the machine.
public enum WatchdUnitAnswer
{
    Yes,
    No,
    Unknown,
    Unsupported,
}
