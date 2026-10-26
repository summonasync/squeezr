using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Query;

public static class FileScope
{
    public static HashSet<uint> Resolve(MetaStore store, Func<FileDocument, bool> predicate, uint? repoId = null)
    {
        var files = repoId is { } id ? store.ListFiles(id) : store.ListAllFiles();
        var fileIds = new HashSet<uint>();
        foreach (var file in files)
        {
            if (predicate(file))
            {
                fileIds.Add(file.Id);
            }
        }

        return fileIds;
    }
}
