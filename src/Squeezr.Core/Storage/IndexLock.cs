using System.Diagnostics;
using System.Globalization;
using System.Text;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed class IndexLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    private IndexLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static IndexLock Acquire(string storeDir)
    {
        var path = Path.Combine(storeDir, SqueezrPaths.LockFileName);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            return TakeOverStale(path);
        }

        WriteOwnerPid(stream);
        return new IndexLock(stream, path);
    }

    private static IndexLock TakeOverStale(string path)
    {
        int? ownerPid;
        try
        {
            ownerPid = ReadOwnerPid(path);
        }
        catch (IOException)
        {
            throw new WorkspaceLockedException();
        }

        if (ownerPid is int alivePid && IsProcessAlive(alivePid))
        {
            throw new WorkspaceLockedException();
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            throw new WorkspaceLockedException();
        }

        var owner = ownerPid is int deadPid
            ? deadPid.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        Console.Error.WriteLine($"warning: stale lock from pid {owner} removed");
        WriteOwnerPid(stream);
        return new IndexLock(stream, path);
    }

    private static void WriteOwnerPid(FileStream stream)
    {
        try
        {
            var pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            stream.Write(pid, 0, pid.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            stream.Dispose();
            throw;
        }
    }

    private static int? ReadOwnerPid(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
