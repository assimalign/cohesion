using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Internal;

// A lazy recovered view over an owned read-only data stream. Only frame offsets and
// page ranges survive the journal scan. The pool caches the pages read by gates.
[System.Diagnostics.DebuggerDisplay("StorageRecoveredStream")]
[System.Diagnostics.DebuggerTypeProxy(typeof(StorageRecoveredStream.DebugView))]
internal sealed class StorageRecoveredStream : Stream
{
    private readonly StorageStream _source;
    private readonly long _sourceLength;
    private Dictionary<long, PageRecords> _records = [];
    private long[] _offsets = [];
    private int _indexCapacity;
    private readonly byte[] _page = new byte[Page.Size];
    private StorageJournal? _journal;
    private CancellationToken _cancellationToken;
    private long _position;
    private long _length;
    private long _recordCount;
    private bool _disposed;

    internal StorageRecoveredStream(StorageStream source)
    {
        _source = source;
        _length = _sourceLength = source.Length;
    }

    internal long RetainedPageBytes => _page.LongLength;
    internal long IndexedRecordCount => _recordCount;
    // Conservative object/array headers, 28 bytes per dictionary capacity slot, and
    // precisely one long per applicable record. Inspection never grows the dictionary.
    internal long RetainedIndexBytes => 160L + 28L * _indexCapacity + sizeof(long) * _offsets.LongLength;

    internal StorageRecoveryResult Initialize(StorageJournal journal, IReadOnlySet<long> protectedPages, CancellationToken cancellationToken)
    {
        _journal = journal;
        _cancellationToken = cancellationToken;
        var analysis = journal.ReadRecoveryAnalysis(cancellationToken, out var token);
        var plans = StorageRecovery.GetPlans(analysis, _sourceLength, protectedPages, cancellationToken);
        int total = 0;
        _records = new Dictionary<long, PageRecords>(plans.Count);
        foreach (var (pageId, plan) in plans)
        {
            _records.Add(pageId, new PageRecords(total, 0));
            total = checked(total + plan.RecordCount);
        }
        _indexCapacity = _records.EnsureCapacity(0);
        _offsets = new long[total];
        long offset = 0;
        foreach (var record in journal.ReadRecoveryRecords(token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StorageRecovery.IsPageRecord(record.Type) && plans.ContainsKey((long)record.PageId)
                && StorageRecovery.ShouldApply(record, analysis.Committed))
            {
                long pageId = (long)record.PageId;
                var range = _records[pageId];
                _offsets[range.Start + range.Count] = offset;
                _records[pageId] = range with { Count = range.Count + 1 };
                _recordCount++;
                _length = Math.Max(_length, (pageId + 1) * Page.Size);
            }
            offset += StorageJournal.RecoveryFrameOverhead + record.Payload.Length;
        }
        return new StorageRecoveryResult(analysis.MaxSequence, analysis.CheckpointLsn, new HashSet<long>(plans.Keys));
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
    public override long Position { get { ObjectDisposedException.ThrowIf(_disposed, this); return _position; } set { ObjectDisposedException.ThrowIf(_disposed, this); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_journal is null)
        {
            int raw = _source.Read(buffer, _position);
            _position += raw;
            return raw;
        }
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
        int read = 0;
        while (read < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long pageId = _position / Page.Size;
            int inPage = (int)(_position % Page.Size);
            int take = Math.Min(count - read, Page.Size - inPage);
            if (_records.TryGetValue(pageId, out var records))
            {
                ReadPage(records);
                _page.AsSpan(inPage, take).CopyTo(buffer[read..]);
            }
            else { ReadSource(_position, buffer.Slice(read, take)); }
            _position += take;
            read += take;
        }
        return count;
    }

    private void ReadPage(PageRecords records)
    {
        Array.Clear(_page);
        bool imaged = false;
        foreach (var record in _journal!.ReadRecoveryPageRecords(_offsets, records.Start, records.Count))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            StorageRecovery.Apply(record, _page, imaged);
            imaged = true;
        }
        PageChecksum.Stamp(_page);
    }

    // An unchanged header read must remain a header read. Reading an unrelated body could
    // introduce an I/O refusal that normal recovery never encounters, as well as needless I/O.
    private void ReadSource(long offset, Span<byte> destination)
    {
        int wanted = (int)Math.Min(destination.Length, Math.Max(0, _sourceLength - offset));
        int read = 0;
        while (read < wanted)
        {
            int next = _source.Read(destination.Slice(read, wanted - read), offset + read);
            if (next == 0) { throw new StorageIOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageStreamReadPageStorageIO, offset / Page.Size)); }
            read += next;
        }
        destination[wanted..].Clear();
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position;
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.RecoveryProbeReadOnly);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.RecoveryProbeReadOnly);
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _records.Clear(); _records.TrimExcess(); _offsets = []; _indexCapacity = 0; _journal = null; _source.Dispose(); }
        base.Dispose(disposing);
    }

    private readonly record struct PageRecords(int Start, int Count);
    private sealed class DebugView
    {
        public DebugView(StorageRecoveredStream value) { _ = value; }
    }
}
