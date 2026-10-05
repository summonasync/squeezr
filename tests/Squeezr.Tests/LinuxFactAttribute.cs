namespace Squeezr.Tests;

// For the facts whose premise is a Linux file mode. xunit v2 has no runtime skip, so the gate belongs to
// discovery: on any other OS the runner reports the test as skipped instead of letting an early return
// report green and pose as coverage the platform never exercised.
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "requires Linux file modes";
        }
    }
}
