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
/// decisions key off the <em>entry's</em> timestamp, so behavior is deterministic under a fake
/// <see cref="TimeProvider"/> upstream.
/// </summary>
internal sealed class W3CAccessLogWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new(capacity: 512);
    private readonly string _directory;
    private readonly string _prefix;
    private readonly AccessLogFormat _format;
    private readonly long? _fileSizeLimit;
    private readonly int? _retainedFileCountLimit;
    private readonly Timer? _flushTimer;

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
        TimeSpan flushInterval)
    {
        _directory = directory;
        _prefix = prefix;
        _format = format;
        _fileSizeLimit = fileSizeLimit;
        _retainedFileCountLimit = retainedFileCountLimit;

        if (flushInterval > TimeSpan.Zero)
        {
            _flushTimer = new Timer(static state => ((W3CAccessLogWriter)state!).TimerFlush(), this, flushInterval, flushInterval);
        }
    }

    public void Write(ILoggerEntry entry)
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
                Open(date, startSequence: 0, entry.Timestamp);
            }
            else if (_fileSizeLimit is { } limit && _approximateSize >= limit)
            {
                Open(date, _sequence + 1, entry.Timestamp);
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

    public void Flush()
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

    public void Dispose()
    {
        // Dispose the timer first: an in-flight callback either finishes before we take the
        // lock or observes _disposed afterwards.
        _flushTimer?.Dispose();

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseWriter();
        }
    }

    private void TimerFlush()
    {
        lock (_lock)
        {
            if (_disposed || !_dirty || _writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _dirty = false;
            }
            catch
            {
                // A transient flush failure (disk pressure, ...) must not take down the timer;
                // the next write or flush retries.
            }
        }
    }

    private void Open(DateOnly date, int startSequence, DateTimeOffset timestamp)
    {
        CloseWriter();

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

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 64 * 1024) { AutoFlush = false };
            _currentPath = path;
            _approximateSize = existingLength;

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

        ApplyRetention();
    }

    private void ApplyRetention()
    {
        if (_retainedFileCountLimit is not { } keep)
        {
            return;
        }

        try
        {
            var files = new List<FileInfo>();
            foreach (string path in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
            {
                files.Add(new FileInfo(path));
            }

            if (files.Count <= keep)
            {
                return;
            }

            files.Sort(static (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));

            int excess = files.Count - keep;
            for (int i = 0; i < files.Count && excess > 0; i++)
            {
                if (string.Equals(files[i].FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    files[i].Delete();
                }
                catch
                {
                    // A file locked by an external reader is skipped; the next roll retries.
                }

                excess--;
            }
        }
        catch
        {
            // Retention is best-effort: enumeration failures must never block logging.
        }
    }

    private void CloseWriter()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
        }
        catch
        {
            // Flush-on-close is best-effort; disposal below still releases the handle.
        }

        _writer.Dispose();
        _writer = null;
        _currentPath = null;
        _dirty = false;
    }

    private string BuildPath(DateOnly date, int sequence)
    {
        string name = sequence == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{date:yyyyMMdd}.log")
            : string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{date:yyyyMMdd}.{sequence:D3}.log");

        return Path.Combine(_directory, name);
    }
}
