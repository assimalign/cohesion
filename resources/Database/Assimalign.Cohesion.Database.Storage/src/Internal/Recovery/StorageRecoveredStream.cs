using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Internal;

// A lazy recovered view over an owned read-only data stream. Only record identities and
// offsets survive the journal scan. The pool, rather than this stream, caches gate-read pages.
[System.Diagnostics.DebuggerDisplay("StorageRecoveredStream")]
[System.Diagnostics.DebuggerTypeProxy(typeof(StorageRecoveredStream.DebugView))]
internal sealed class StorageRecoveredStream : Stream
{
    private readonly StorageStream _source;
    private readonly long _sourceLength;
    private readonly Dictionary<long, List<RecordReference>> _records = [];
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
    internal long RetainedIndexBytes
    {
        get
        {
            long bytes = 128L + 36L * _records.EnsureCapacity(0);
            foreach (var records in _records.Values)
            {
                bytes += 64L + (long)records.Capacity * System.Runtime.CompilerServices.Unsafe.SizeOf<RecordReference>();
            }
            return bytes;
        }
    }

    internal StorageRecoveryResult Initialize(StorageJournal journal, IReadOnlySet<long> protectedPages, CancellationToken cancellationToken)
    {
        _journal = journal;
        _cancellationToken = cancellationToken;
        var analysis = journal.ReadRecoveryAnalysis(cancellationToken, out var token);
        var plans = StorageRecovery.GetPlans(analysis, _sourceLength, protectedPages, cancellationToken);
        long offset = 0;
        foreach (var record in journal.ReadRecoveryRecords(token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StorageRecovery.IsPageRecord(record.Type) && plans.ContainsKey((long)record.PageId)
                && StorageRecovery.ShouldApply(record, analysis.Committed))
            {
                long pageId = (long)record.PageId;
                if (!_records.TryGetValue(pageId, out var records)) { _records.Add(pageId, records = []); }
                records.Add(new RecordReference(record.Lsn, record.TransactionSequence, record.Type,
                    offset + StorageJournal.RecoveryPayloadOffset, record.Payload.Length));
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
                ReadPage(pageId, records);
                _page.AsSpan(inPage, take).CopyTo(buffer[read..]);
                Array.Clear(_page); // Retain no bytes from a header-only owner-scan read.
            }
            else { ReadSource(_position, buffer.Slice(read, take)); }
            _position += take;
            read += take;
        }
        return count;
    }

    private void ReadPage(long pageId, List<RecordReference> records)
    {
        Array.Clear(_page);
        bool imaged = false;
        foreach (var reference in records)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var payload = new byte[reference.Length];
            _journal!.ReadRecoveryPayload(reference.Offset, payload);
            StorageRecovery.Apply(new JournalRecord(reference.Lsn, reference.TransactionSequence, reference.Type, (PageId)pageId, payload), _page, imaged);
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
        if (disposing && !_disposed) { _disposed = true; _records.Clear(); _records.TrimExcess(); _journal = null; _source.Dispose(); }
        base.Dispose(disposing);
    }

    private readonly record struct RecordReference(long Lsn, long TransactionSequence, JournalRecordType Type, long Offset, int Length);
    private sealed class DebugView
    {
        public DebugView(StorageRecoveredStream value) { _ = value; }
    }
}
