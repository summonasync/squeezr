using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed record QueryTelemetryEntry(
    [property: JsonPropertyName("ts")] DateTime Ts,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("params")] object? Params,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("hits")] int Hits,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("cacheHit")] bool CacheHit,
    [property: JsonPropertyName("indexAgeSeconds")] long? IndexAgeSeconds,
    [property: JsonPropertyName("client"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null,
    [property: JsonPropertyName("clientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientVersion = null,
    [property: JsonPropertyName("cacheInvalidated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CacheInvalidated = false,
    [property: JsonPropertyName("baselineTokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BaselineTokens = null,
    [property: JsonPropertyName("mapped"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Mapped = null);

public static class QueryTelemetry
{
    // what a recorded parameter value becomes when the guard says it names a protected path — the line stays
    // (the counters over queries.jsonl are the reason the file exists), only the name leaves it
    public const string RedactedPlaceholder = "[secret-guard withheld]";

    // The append primitive has to survive two writers sharing the file. File.AppendAllText opens with
    // FileShare.Read, and that is not enough: append placement is settled when the file is opened, not when the
    // bytes are written, so two handles that open before the other's bytes land overwrite one another with no
    // error anywhere (measured on Linux — audits/107-gate-2026-11-09/probe-append-primitive.log: eight writers
    // released from a barrier go bad in all 200 trials, losing up to seven of eight rows, whichever share mask
    // is asked for). The writer lock is the only real exclusion; this gate is the same exclusion inside one
    // process, and FileShare.Delete is what lets a swap rename the log on Windows while a handle is still open.
    private static readonly object UnsynchronisedAppendGate = new();

    private static void AppendLine(string path, string line)
    {
        lock (UnsynchronisedAppendGate)
        {
            using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
    }

    public static void Append(string workspaceRoot, QueryTelemetryEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry) + '\n';
            var logPath = SqueezrPaths.QueriesLogPath(workspaceRoot);
            var storeDir = SqueezrPaths.StoreDir(workspaceRoot);
            // A row that missed the first budget is late either way, so it waits a second, longer one rather than
            // writing anyway: outside the lock a line can land on top of a queued holder's line as easily as into
            // a file a swap is about to rename, and neither is noticed by anybody but the row that vanished. The
            // unsynchronised write is the last resort, reachable only when both budgets are gone.
            using (var writer = TelemetryWriteLock.TryAcquire(storeDir))
            {
                if (writer is not null)
                {
                    AppendLine(logPath, line);
                    return;
                }
            }

            using (var late = TelemetryWriteLock.TryAcquire(storeDir, TelemetryWriteLock.LateAppendWait))
            {
                if (late is not null)
                {
                    AppendLine(logPath, line);
                    return;
                }
            }

            AppendLine(logPath, line);
        }
        catch
        {
        }
    }

    // `truncated` means the answer lost rows the caller asked for; `mapped` says the budget traded a body for the
    // member map instead of losing anything. A caller that has a state passes both — a null `mapped` is left for
    // the rows written before the split, plus the surfaces that record no budget state at all (hook pre-read),
    // and stats reads the absence as "this row cannot be split", never as "not mapped".
    public static void Record(
        string workspaceRoot,
        Guid sessionId,
        string tool,
        object? parameters,
        int tokensUsed,
        bool truncated,
        int hits,
        long durationMs,
        bool cacheHit = false,
        TimeSpan? indexAge = null,
        string? client = "cli",
        string? clientVersion = null,
        bool cacheInvalidated = false,
        long? baselineTokens = null,
        bool? mapped = null) =>
        Append(workspaceRoot, new QueryTelemetryEntry(
            DateTime.UtcNow, sessionId, tool, parameters, tokensUsed, truncated,
            hits, durationMs, cacheHit, indexAge is null ? null : (long)indexAge.Value.TotalSeconds,
            client, clientVersion, cacheInvalidated, baselineTokens, mapped));

    public static IReadOnlyList<QueryTelemetryEntry> ReadAll(string workspaceRoot)
    {
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var entries = new List<QueryTelemetryEntry>();
            foreach (var line in ReadLogLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<QueryTelemetryEntry>(line);
                    if (parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static int Count(string workspaceRoot)
    {
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return ReadLogLines(path).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // A reader that does not share delete is, on Windows, a lock on the rename the scrub is trying to finish: a
    // stats call sitting next to a compact would make the pass throw and report an already-clean log. Every read
    // of the log therefore takes the share mask the writers take.
    private static IEnumerable<string> ReadLogLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (text.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    // reset empties the file, which makes it the one writer whose effect an append that slipped past the count
    // would silently reverse — so it joins the same lock append and scrub queue on. Telemetry appends fail open,
    // but reset is asked destruction: a holder that outlasts the budget means the file is not this process's to
    // truncate, and refusing clears nothing rather than half of somebody's race. onLockHeld runs where the lock
    // is exclusive — the deterministic seam the interleaving pins are written through.
    public static int Reset(string workspaceRoot, Action? onLockHeld = null)
    {
        using var writer = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(workspaceRoot));
        if (writer is null)
        {
            throw new WorkspaceException(
                "stats --reset refused: the telemetry write lock stayed busy past the wait budget; nothing was cleared");
        }

        onLockHeld?.Invoke();

        var removed = Count(workspaceRoot);
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        return removed;
    }

    // Recorded parameters are the one place where a query that ran before the guard existed keeps quoting the
    // path it asked about, so the purge pass rewrites them through the same classifier that refused the read.
    // Only values the classifier calls a protected path are replaced; lines it cannot parse and values it does
    // not recognise are written back byte for byte, which is what keeps the counters over queries.jsonl honest.
    public static int RedactProtectedParams(string workspaceRoot, Func<string, bool> isProtectedPath) =>
        RedactProtectedParams(workspaceRoot, isProtectedPath, out _);

    public static int RedactProtectedParams(
        string workspaceRoot, Func<string, bool> isProtectedPath, out bool lockBusy) =>
        RedactProtectedParams(workspaceRoot, isProtectedPath, out lockBusy, null);

    // The lock guards the swap, not the reading of it. Classification is the slow half of this pass and it used
    // to run inside the writer lock, which pushed every queued append past its wait budget and into an
    // unsynchronised write — and a log written outside the lock is not safe to write at all, because append
    // placement is fixed when the file is opened, so two racing writers overwrite one another silently. Under
    // the lock this pass now costs the log two byte reads and a rename; the CPU between them is what a scrub of
    // a big file actually takes, and no writer has to queue through it.
    // beforeTheSwap runs inside the lock with only the rename left to do — the deterministic seam the
    // interleaving pins watch exclusivity through, the way Reset's onLockHeld is.
    internal static int RedactProtectedParams(
        string workspaceRoot, Func<string, bool> isProtectedPath, out bool lockBusy, Action? beforeTheSwap)
    {
        lockBusy = false;
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        var storeDir = SqueezrPaths.StoreDir(workspaceRoot);
        if (!File.Exists(path))
        {
            return 0;
        }

        byte[] snapshot;
        using (var reader = TelemetryWriteLock.TryAcquire(storeDir))
        {
            if (reader is null)
            {
                lockBusy = true;
                return 0;
            }

            try
            {
                snapshot = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        var (lines, snapshotBytes) = TakeCompleteLines(snapshot);
        var redacted = RedactLines(lines, isProtectedPath);
        if (redacted == 0)
        {
            return 0;
        }

        using var swapper = TelemetryWriteLock.TryAcquire(storeDir);
        if (swapper is null)
        {
            lockBusy = true;
            return 0;
        }

        byte[] live;
        try
        {
            live = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        // A reset that ran while we were classifying replaced the log under us. Publishing the redacted prefix we
        // read earlier would resurrect rows the caller deleted, so the pass is abandoned when the bytes that hold
        // our snapshot are no longer the bytes on disk.
        if (live.Length < snapshotBytes ||
            !live.AsSpan(0, snapshotBytes).SequenceEqual(snapshot.AsSpan(0, snapshotBytes)))
        {
            return 0;
        }

        // Whatever an append landed while the classification was running is part of the log the swap publishes:
        // dropping it here is exactly the row loss this pass used to cause, so it is scrubbed and carried too.
        var (arrived, arrivedBytes) = TakeCompleteLines(live.AsSpan(snapshotBytes));
        if (arrived.Length > 0)
        {
            redacted += RedactLines(arrived, isProtectedPath);
            lines = [.. lines, .. arrived];
        }

        var publishedBytes = snapshotBytes + arrivedBytes;
        var tempPath = path + ".scrub.tmp";
        // A pass killed between the temp write and the rename leaves the temp behind for good. Nothing else can be
        // mid-swap while this lock is held, so a temp sitting here is stale by definition and gets reclaimed.
        TryDeleteTemp(tempPath);
        try
        {
            // joined explicitly so a scrub run on one platform does not rewrite the line endings of a log
            // another platform appended
            File.WriteAllText(tempPath, string.Join('\n', lines) + '\n');

            // The handle is taken before the rename and read after it: it keeps pointing at the inode the path
            // just gave up, so whatever a writer pushed into that inode while the swap was running is still
            // reachable and gets carried into the published log instead of being orphaned with it. Share.Delete is
            // not decoration: on Windows a destination held without it cannot be replaced, so the rename would
            // throw and the pass would report "nothing to redact" forever on the platform that ships the installer.
            using var orphaned = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            beforeTheSwap?.Invoke();
            File.Move(tempPath, path, overwrite: true);
            var swept = TakeCompleteLines(ReadBytesFrom(orphaned, publishedBytes)).Lines;
            if (swept.Length > 0)
            {
                redacted += RedactLines(swept, isProtectedPath);
                AppendLine(path, string.Join('\n', swept) + '\n');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            // Nothing was published, so a bare zero would read as "this log holds no protected names" while the
            // values are still in it. The pass reports the same skip the busy lock reports: the caller's warning
            // names the lock as the usual cause, and the swap failing under a reader is its rarer twin.
            lockBusy = true;
            return 0;
        }

        return redacted;
    }

    private static int RedactLines(string[] lines, Func<string, bool> isProtectedPath)
    {
        var redacted = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(lines[i]) is not JsonObject entry)
                {
                    continue;
                }

                if (entry["params"] is null)
                {
                    continue;
                }

                if (RedactTree(entry["params"]!, isProtectedPath, ref redacted) > 0)
                {
                    lines[i] = entry.ToJsonString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return redacted;
    }

    // Whole lines only, both for the snapshot and for the arrived tail: a writer caught mid-line leaves its bytes
    // out of the swap instead of being published as two halves of a corrupt record. What completed after the last
    // read is stranded with the inode it was written into — reachable by nobody, which is the price a row pays
    // when it had to write without the lock at all. Never a half-published record, only a late one.
    private static (string[] Lines, int Bytes) TakeCompleteLines(ReadOnlySpan<byte> raw)
    {
        var lastBreak = raw.LastIndexOf((byte)'\n');
        if (lastBreak < 0)
        {
            return ([], 0);
        }

        return (Encoding.UTF8.GetString(raw[..(lastBreak + 1)]).Split('\n')[..^1], lastBreak + 1);
    }

    // the length is taken once and read to: a writer appending while the tail is copied only adds bytes the next
    // pass will see, it never shifts the window this one is reading
    private static byte[] ReadBytesFrom(Stream stream, int offset)
    {
        var length = stream.Length;
        if (length <= offset)
        {
            return [];
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length - offset];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static int RedactTree(JsonNode node, Func<string, bool> isProtectedPath, ref int redacted)
    {
        var hits = 0;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (NamesAProtectedPath(property.Value, isProtectedPath))
                {
                    obj[property.Key] = JsonValue.Create(RedactedPlaceholder);
                    redacted++;
                    hits++;
                    continue;
                }

                if (property.Value is not null)
                {
                    hits += RedactTree(property.Value, isProtectedPath, ref redacted);
                }
            }

            return hits;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (NamesAProtectedPath(array[i], isProtectedPath))
                {
                    array[i] = JsonValue.Create(RedactedPlaceholder);
                    redacted++;
                    hits++;
                    continue;
                }

                if (array[i] is not null)
                {
                    hits += RedactTree(array[i]!, isProtectedPath, ref redacted);
                }
            }
        }

        return hits;
    }

    private static bool NamesAProtectedPath(JsonNode? node, Func<string, bool> isProtectedPath) =>
        node is JsonValue value && value.TryGetValue(out string? text) && isProtectedPath(text);

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
