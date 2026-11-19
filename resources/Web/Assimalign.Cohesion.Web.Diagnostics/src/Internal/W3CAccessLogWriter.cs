using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

using Assimalign.Cohesion.Logging;

/// <summary>
/// The file sink behind <see cref="W3CAccessLogProvider"/>: buffered, lock-serialized writes
/// into per-UTC-day files (<c>{prefix}-{yyyyMMdd}.log</c>), size-based rolling into sequenced
/// files within a day, oldest-first retention sweeps, and a periodic flush timer. Rolling
/// decisions key off the <em>entry's</em> timestamp, and retention reads that date back from the
/// file names, so behavior is deterministic under a fake <see cref="TimeProvider"/> upstream.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("W3CAccessLogWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class W3CAccessLogWriter : IDisposable
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new(capacity: 512);
    private readonly string _directory;
    private readonly string _prefix;
    private readonly AccessLogFormat _format;
    private readonly long? _fileSizeLimit;
    private readonly int? _retainedFileCountLimit;
    private readonly Timer? _flushTimer;
    private readonly Func<string, Stream>? _streamFactory;

    private StreamWriter? _writer;
    private string? _currentPath;
    private DateOnly _currentDate;
    private int _sequence;
    private long _approximateSize;
    private bool _dirty;
    private bool _disposed;

    public W3CAccessLogWriter(
        string directory,
        string prefix,
        AccessLogFormat format,
        long? fileSizeLimit,
        int? retainedFileCountLimit,
        TimeSpan flushInterval,
        Func<string, Stream>? streamFactory = null)
    {
        _directory = directory;
        _prefix = prefix;
        _format = format;
        _fileSizeLimit = fileSizeLimit;
        _retainedFileCountLimit = retainedFileCountLimit;
        _streamFactory = streamFactory;

        if (flushInterval > TimeSpan.Zero)
        {
            _flushTimer = new Timer(static state => ((W3CAccessLogWriter)state!).TimerFlush(), this, flushInterval, flushInterval);
        }
    }

    public void Write(ILoggerEntry entry)
    {
        var diagnostics = new PendingDiagnostics();
        Exception? failure = null;
        try
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                DateOnly date = DateOnly.FromDateTime(entry.Timestamp.UtcDateTime);

                if (_writer is null || date != _currentDate)
                {
                    Open(date, startSequence: 0, entry.Timestamp, ref diagnostics);
                }
                else if (_fileSizeLimit is { } limit && _approximateSize >= limit)
                {
                    Open(date, _sequence + 1, entry.Timestamp, ref diagnostics);
                }

                _buffer.Clear();
                W3CAccessLogFormatter.AppendLine(_buffer, entry, _format);
                _writer!.Write(_buffer);

                // Char count approximates bytes (the line is ASCII-dominant); the size limit is
                // documented as approximate.
                _approximateSize += _buffer.Length;

                if (_flushTimer is null)
                {
                    _writer.Flush();
                }
                else
                {
                    _dirty = true;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            // EventListener callbacks are synchronous. Never invoke them while holding _lock.
            diagnostics.Report();
            if (failure is not null)
            {
                HttpLoggingEventSource.Log.WriteFailed("Write", failure);
            }
        }
    }

    public void Flush()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed || _writer is null)
                {
                    return;
                }

                _writer.Flush();
                _dirty = false;
            }
        }
        catch (Exception exception)
        {
            HttpLoggingEventSource.Log.WriteFailed("Flush", exception);
            throw;
        }
    }

    public void Dispose()
    {
        // Dispose the timer first: an in-flight callback either finishes before we take the
        // lock or observes _disposed afterwards.
        _flushTimer?.Dispose();

        var diagnostics = new PendingDiagnostics();
        try
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                CloseWriter(ref diagnostics);
            }
        }
        finally
        {
            diagnostics.Report();
        }
    }

    private void TimerFlush()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed || !_dirty || _writer is null)
                {
                    return;
                }

                _writer.Flush();
                _dirty = false;
            }
        }
        catch (Exception exception)
        {
            // Keep the timer alive, but report data loss: StreamWriter may already have
            // discarded the failed buffer. A later flush cannot recover those entries.
            HttpLoggingEventSource.Log.WriteFailed("Flush", exception);
        }
    }

    private void Open(DateOnly date, int startSequence, DateTimeOffset timestamp, ref PendingDiagnostics diagnostics)
    {
        CloseWriter(ref diagnostics);

        _currentDate = date;
        _sequence = startSequence;

        Directory.CreateDirectory(_directory);

        // Skip sequences whose files are already full - a restart appends to the day's current
        // file rather than overwriting or endlessly re-rolling.
        while (true)
        {
            string path = BuildPath(date, _sequence);
            long existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;

            if (_fileSizeLimit is { } limit && existingLength >= limit)
            {
                _sequence++;
                continue;
            }

            Stream stream = _streamFactory is null
                ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
                : _streamFactory(path);
            _writer = new StreamWriter(stream, _utf8NoBom, bufferSize: 64 * 1024) { AutoFlush = false };
            _currentPath = path;
            _approximateSize = existingLength;
            diagnostics.OpenedPath = path;
            diagnostics.Sequence = _sequence;
            diagnostics.ExistingBytes = existingLength;

            if (_format == AccessLogFormat.W3CExtended && existingLength == 0)
            {
                _buffer.Clear();
                W3CAccessLogFormatter.AppendExtendedDirectives(_buffer, timestamp);
                _writer.Write(_buffer);
                _approximateSize += _buffer.Length;
                _dirty = true;
            }

            break;
        }

        ApplyRetention(ref diagnostics);
    }

    private void ApplyRetention(ref PendingDiagnostics diagnostics)
    {
        if (_retainedFileCountLimit is not { } keep)
        {
            return;
        }

        try
        {
            var files = new List<RetentionCandidate>();
            foreach (string path in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
            {
                if (RetentionCandidate.TryParse(path, _prefix, out RetentionCandidate candidate))
                {
                    files.Add(candidate);
                }
            }

            if (files.Count <= keep)
            {
                return;
            }

            // Oldest first by the date and sequence in each name, never by last-write time: files
            // written within one clock tick share a last-write time, and the tie then falls to the
            // enumeration order, which is name order on NTFS but hash order on ext4.
            files.Sort(RetentionCandidate.Compare);

            // Compare names: every enumerated path and _currentPath sit in _directory, and a full
            // path never equals _currentPath when _directory is relative.
            string? currentName = Path.GetFileName(_currentPath);
            int excess = files.Count - keep;
            for (int i = 0; i < files.Count && excess > 0; i++)
            {
                if (string.Equals(files[i].Name, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(files[i].FilePath);
                }
                catch (Exception exception)
                {
                    // A file locked by an external reader is skipped; the next roll retries.
                    (diagnostics.RetentionFailures ??= new()).Add(exception);
                }

                excess--;
            }
        }
        catch (Exception exception)
        {
            // Retention is best-effort: enumeration failures must never block logging.
            (diagnostics.RetentionFailures ??= new()).Add(exception);
        }
    }

    private void CloseWriter(ref PendingDiagnostics diagnostics)
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
        }
        catch (Exception exception)
        {
            // Flush-on-close is best-effort; disposal below still releases the handle.
            diagnostics.CloseFlushFailure = exception;
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception exception)
        {
            diagnostics.CloseDisposeFailure = exception;
            throw;
        }
        finally
        {
            _writer = null;
            _currentPath = null;
            _dirty = false;
        }
    }

    // Operation-local storage keeps callbacks outside the writer lock, including when a
    // nested open/retention/close operation fails. Ordinary writes allocate no diagnostic list.
    private struct PendingDiagnostics
    {
        public string? OpenedPath;
        public int Sequence;
        public long ExistingBytes;
        public Exception? CloseFlushFailure;
        public Exception? CloseDisposeFailure;
        public List<Exception>? RetentionFailures;

        public readonly void Report()
        {
            if (CloseFlushFailure is not null)
            {
                HttpLoggingEventSource.Log.WriteFailed("Close", CloseFlushFailure);
            }
            if (CloseDisposeFailure is not null)
            {
                HttpLoggingEventSource.Log.WriteFailed("Close", CloseDisposeFailure);
            }
            if (OpenedPath is not null)
            {
                HttpLoggingEventSource.Log.FileOpened(OpenedPath, Sequence, ExistingBytes);
            }
            if (RetentionFailures is not null)
            {
                foreach (Exception exception in RetentionFailures)
                {
                    HttpLoggingEventSource.Log.RetentionFailed(exception);
                }
            }
        }
    }

    private string BuildPath(DateOnly date, int sequence)
    {
        string name = sequence == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{date:yyyyMMdd}.log")
            : string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{date:yyyyMMdd}.{sequence:D3}.log");

        return Path.Combine(_directory, name);
    }

    // A file the retention sweep may delete: one whose name BuildPath could have produced.
    private readonly record struct RetentionCandidate(DateOnly Date, int Sequence, string Name, string FilePath)
    {
        private const string extension = ".log";

        // Accepts {prefix}-{yyyyMMdd}.log as the day's sequence 0 and {prefix}-{yyyyMMdd}.{n}.log as
        // sequence n. The prefix is checked literally because on Unix a prefix may contain '*' or
        // '?', which widen the enumeration pattern; another prefix such as "access-api" never
        // parses under "access", since "api-..." is not a date.
        public static bool TryParse(string path, string prefix, out RetentionCandidate candidate)
        {
            candidate = default;
            string name = Path.GetFileName(path);
            ReadOnlySpan<char> rest = name;

            if (rest.Length <= prefix.Length
                || !rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || rest[prefix.Length] != '-')
            {
                return false;
            }

            rest = rest[(prefix.Length + 1)..];
            if (!rest.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            rest = rest[..^extension.Length];
            if (rest.Length < 8
                || !DateOnly.TryParseExact(rest[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                return false;
            }

            int sequence = 0;
            if (rest.Length > 8
                && (rest[8] != '.' || !int.TryParse(rest[9..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)))
            {
                return false;
            }

            candidate = new RetentionCandidate(date, sequence, name, path);
            return true;
        }

        // Oldest first. Sequences compare as numbers, so .1000 follows .999 and the unsequenced
        // file leads its day. The ordinal name tie-break makes the order total, so the unstable
        // List<T>.Sort cannot depend on enumeration order.
        public static int Compare(RetentionCandidate left, RetentionCandidate right)
        {
            int order = left.Date.CompareTo(right.Date);
            if (order == 0)
            {
                order = left.Sequence.CompareTo(right.Sequence);
            }

            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        }
    }

    private sealed class DebugView
    {
        public DebugView(W3CAccessLogWriter value)
        {
        }
    }
}
