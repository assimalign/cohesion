using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class LogSegmentStore(string directory)
{
    private const long maxSegmentBytes = 16 * 1024 * 1024;
    private readonly Channel<QueuedLog> _pending = Channel.CreateBounded<QueuedLog>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private volatile bool _failed;
    private volatile bool _stopped;
    private string? _file;
    private string? _day;
    private int _segment;
    private SegmentIndex? _index;
    private long _queuedBytes;

    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal bool IsAvailable => !_failed && !_stopped;
    internal bool TryEnqueue(StoredLog record)
    {
        if (!IsAvailable) { return false; }
        long bytes = 256L + 2L * (record.T.Length + record.Body.Length + record.Cat.Length + record.Svc.Length + record.SevText.Length);
        foreach (var attribute in record.Attrs) { bytes += 128L + 2L * (attribute.Key.Length + attribute.Value.GetRawText().Length); }
        if (Interlocked.Add(ref _queuedBytes, bytes) <= 64 * 1024 * 1024 && _pending.Writer.TryWrite(new QueuedLog(record, bytes))) { return true; }
        Interlocked.Add(ref _queuedBytes, -bytes);
        return false;
    }

    private sealed record QueuedLog(StoredLog Record, long Bytes);

    // Only SegmentFlushService's dedicated thread invokes this method.
    internal void Flush(bool stopping = false)
    {
        lock (_gate)
        {
            if (stopping)
            {
                _stopped = true;
                _pending.Writer.TryComplete();
            }
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                while (_pending.Reader.TryPeek(out QueuedLog? queued))
                {
                    StoredLog record = queued.Record;
                    byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, LogSpaceJsonContext.Default.StoredLog) + "\n");
                    string day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    if (_file is null || _day != day || new FileInfo(_file).Length + line.Length > maxSegmentBytes)
                    {
                        _day = day;
                        // A fresh segment on restart avoids reopening or rewriting any earlier data.
                        do { _file = Path.Combine(DirectoryPath, $"logs-{day}-{_segment++:D4}.ndjson"); }
                        while (File.Exists(_file));
                        using (File.Create(_file)) { }
                        _index = null;
                    }
                    using (var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(line);
                        stream.Flush(flushToDisk: true);
                    }
                    _pending.Reader.TryRead(out _);
                    Interlocked.Add(ref _queuedBytes, -queued.Bytes);
                    _index = new SegmentIndex(_index?.First ?? record.T, record.T, (_index?.Count ?? 0) + 1);
                    File.WriteAllBytes(_file + ".index", JsonSerializer.SerializeToUtf8Bytes(_index, LogSpaceJsonContext.Default.SegmentIndex));
                }
                _failed = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                // Retain queued records for the next flush attempt. Ingest sees 503 while the store is unavailable.
            }
        }
    }

    internal (string Body, string? Cursor) Query(string? resource, DateTimeOffset? since, int limit, string? cursor)
    {
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource + "\n" + since?.ToString("O"))));
        QueryCursor? resume = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try { resume = JsonSerializer.Deserialize(Convert.FromBase64String(cursor), LogSpaceJsonContext.Default.QueryCursor); }
            catch (Exception exception) when (exception is JsonException or FormatException)
            { throw new ArgumentException("Invalid log query cursor.", nameof(cursor), exception); }
            if (resume is null || resume.Filter != fingerprint || resume.Offset < 0 || resume.File != Path.GetFileName(resume.File))
            { throw new ArgumentException("The cursor does not belong to this query.", nameof(cursor)); }
        }
        decimal? minimum = since is { } instant ? (decimal)(instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100 : null;
        lock (_gate)
        {
            var body = new StringBuilder();
            int count = 0;
            if (!Directory.Exists(DirectoryPath)) { return (string.Empty, null); }
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "logs-*.ndjson").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);
                if (resume is not null && string.CompareOrdinal(name, resume.File) < 0) { continue; }
                long offset = 0;
                foreach (string line in File.ReadLines(file))
                {
                    offset++;
                    if (resume is not null && name == resume.File && offset <= resume.Offset) { continue; }
                    StoredLog? record;
                    try { record = JsonSerializer.Deserialize(line, LogSpaceJsonContext.Default.StoredLog); }
                    catch (JsonException) { continue; } // Ignore a crash-truncated final line.
                    if (record is null || (!string.IsNullOrEmpty(resource) && record.Svc != resource) ||
                        (minimum is not null && (!decimal.TryParse(record.T, CultureInfo.InvariantCulture, out decimal time) || time < minimum)))
                    { continue; }
                    if (body.Length + line.Length > 4 * 1024 * 1024 && count > 0)
                    {
                        string next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new QueryCursor(name, offset - 1, fingerprint),
                            LogSpaceJsonContext.Default.QueryCursor));
                        return (body.ToString(), next);
                    }
                    body.AppendLine(line);
                    if (++count == limit)
                    {
                        string next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new QueryCursor(name, offset, fingerprint),
                            LogSpaceJsonContext.Default.QueryCursor));
                        return (body.ToString(), next);
                    }
                }
            }
            return (body.ToString(), null);
        }
    }
}
