using System.Diagnostics;

namespace Squeezr.Cli.Output;

public static class DaemonResourceSampler
{
    // proc(5) reports utime/stime in clock ticks of USER_HZ; every shipped kernel answers
    // sysconf(_SC_CLK_TCK) with 100 and .NET exposes no sysconf, so the rate is pinned here once.
    internal const long ClockTicksPerSecond = 100;

    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private static readonly DaemonResourceSample Unknown = new(null, null);

    public static IReadOnlyDictionary<int, DaemonResourceSample> Sample(
        IReadOnlyCollection<int> pids,
        TimeSpan? window = null,
        Func<int, DaemonProcReading?>? readProc = null,
        Action<TimeSpan>? wait = null)
    {
        var ids = pids.Distinct().ToArray();
        if (!OperatingSystem.IsLinux())
        {
            return ids.ToDictionary(pid => pid, _ => Unknown);
        }

        var read = readProc ?? ReadLive;
        var span = window ?? Window;
        var startedAt = Stopwatch.GetTimestamp();
        var first = ReadAll(ids, read);
        (wait ?? Thread.Sleep)(span);
        var wall = Stopwatch.GetElapsedTime(startedAt);
        var second = ReadAll(ids, read);
        return first.ToDictionary(row => row.Key, row => Describe(row.Value, second[row.Key], wall));
    }

    internal static DaemonResourceSample Describe(DaemonProcReading? first, DaemonProcReading? second, TimeSpan wall)
    {
        if (first is null || second is null || wall <= TimeSpan.Zero)
        {
            return Unknown;
        }

        var tickDelta = second.CpuTicks - first.CpuTicks;
        if (tickDelta < 0 || second.RssPages < 0)
        {
            return Unknown;
        }

        return new DaemonResourceSample(
            tickDelta / (double)ClockTicksPerSecond / wall.TotalSeconds * 100,
            second.RssPages * Environment.SystemPageSize);
    }

    private static IReadOnlyDictionary<int, DaemonProcReading?> ReadAll(
        IEnumerable<int> pids,
        Func<int, DaemonProcReading?> read) =>
        pids.ToDictionary(pid => pid, pid => TryRead(pid, read));

    private static DaemonProcReading? TryRead(int pid, Func<int, DaemonProcReading?> read)
    {
        try
        {
            return read(pid);
        }
        catch (Exception)
        {
            // the sampler sits under the fleet table: a pid exploding mid-read is n/a data, never a failed command
            return null;
        }
    }

    private static DaemonProcReading? ReadLive(int pid)
    {
        var ticks = ReadCpuTicks(pid);
        var pages = ReadRssPages(pid);
        return ticks is null || pages is null ? null : new DaemonProcReading(ticks.Value, pages.Value);
    }

    private static long? ReadCpuTicks(int pid)
    {
        var line = File.ReadAllText($"/proc/{pid}/stat");
        var fields = line[(line.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13
            || !long.TryParse(fields[11], out var utime)
            || !long.TryParse(fields[12], out var stime))
        {
            return null;
        }

        return utime + stime;
    }

    private static long? ReadRssPages(int pid)
    {
        var fields = File.ReadAllText($"/proc/{pid}/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && long.TryParse(fields[1], out var resident) ? resident : null;
    }
}
