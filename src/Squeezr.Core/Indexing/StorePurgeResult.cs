namespace Squeezr.Core.Indexing;

// What one store purge took out of the index: never which paths. A purge that listed what it removed would be
// the biggest existence oracle in the product — the report is numbers, and only numbers.
// ReapsQueued counts point cleanups the drop owed but could not run, which the reap journal and
// 'squeezr vectors reconcile --apply' already know how to finish.
public sealed record StorePurgeResult(
    int PathsPurged,
    int RowsPurged,
    int ReapJournalRedacted,
    int ReapsQueued);
