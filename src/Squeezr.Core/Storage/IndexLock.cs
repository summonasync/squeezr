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
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new IndexLock(stream, path);
        }
        catch (IOException)
        {
            throw new WorkspaceLockedException();
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
