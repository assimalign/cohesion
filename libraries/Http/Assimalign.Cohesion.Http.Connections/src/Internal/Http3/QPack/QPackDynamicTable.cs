using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// The QPACK dynamic table (RFC 9204 §3.2) maintained by the decoder side of an
/// HTTP/3 connection. Entries are inserted by the peer's encoder-stream
/// instructions (Set Dynamic Table Capacity, Insert with Name Reference, Insert
/// with Literal Name, Duplicate — §4.3) and referenced by field sections using
/// absolute, relative, and post-base indices (§3.2.4–§3.2.6).
/// </summary>
/// <remarks>
/// <para>
/// The table uses <em>absolute indexing</em>: the first inserted entry has
/// absolute index 0 and each subsequent insertion increments the index.
/// <see cref="InsertCount"/> is the running total of insertions and never
/// decreases, even as old entries are evicted. Entry size is the octet length of
/// the name plus the octet length of the value plus 32 (§3.2.1); names and
/// values are Latin-1, so a string's <see cref="string.Length"/> equals its
/// octet count.
/// </para>
/// <para>
/// AOT posture: the table is a plain list plus integer bookkeeping — no
/// reflection, no runtime code generation.
/// </para>
/// </remarks>
internal sealed class QPackDynamicTable
{
    private const int EntryOverhead = 32; // RFC 9204 §3.2.1.

    private readonly List<Entry> _entries = new(); // Oldest → newest.
    private readonly long _maxCapacity;
    private long _capacity;
    private long _size;
    private long _droppedCount; // Number of entries evicted; absolute index base.

    /// <summary>
    /// Initializes a dynamic table whose capacity may never exceed
    /// <paramref name="maxCapacity"/> (the value the decoder advertised as
    /// <c>QPACK_MAX_TABLE_CAPACITY</c>). The working capacity starts at 0; the
    /// encoder must raise it with a Set Dynamic Table Capacity instruction
    /// (RFC 9204 §4.3.1) before inserting.
    /// </summary>
    /// <param name="maxCapacity">The maximum capacity the decoder allows.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxCapacity"/> is negative.
    /// </exception>
    public QPackDynamicTable(long maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        _maxCapacity = maxCapacity;
    }

    /// <summary>Gets the maximum capacity the decoder advertised.</summary>
    public long MaxCapacity => _maxCapacity;

    /// <summary>Gets the current working capacity (last Set Dynamic Table Capacity value).</summary>
    public long Capacity => _capacity;

    /// <summary>Gets the current total size of all entries, in octets.</summary>
    public long Size => _size;

    /// <summary>
    /// Gets the total number of insertions performed over the connection's
    /// lifetime (the absolute-index basis, RFC 9204 §3.2.4). Never decreases.
    /// </summary>
    public long InsertCount => _droppedCount + _entries.Count;

    /// <summary>
    /// Applies a Set Dynamic Table Capacity instruction (RFC 9204 §3.2.3 /
    /// §4.3.1), evicting entries as needed to fit the new capacity.
    /// </summary>
    /// <param name="capacity">The requested capacity, in octets.</param>
    /// <exception cref="QPackException">
    /// Thrown (<c>QPACK_ENCODER_STREAM_ERROR</c>) when the requested capacity is
    /// negative or exceeds the advertised maximum.
    /// </exception>
    public void SetCapacity(long capacity)
    {
        if (capacity < 0 || capacity > _maxCapacity)
        {
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                $"QPACK dynamic table capacity {capacity} is invalid or exceeds the advertised maximum {_maxCapacity}.");
        }

        _capacity = capacity;
        EvictToFit(0);
    }

    /// <summary>
    /// Inserts an entry with a literal name (RFC 9204 §4.3.3).
    /// </summary>
    /// <param name="name">The (Latin-1) field name.</param>
    /// <param name="value">The (Latin-1) field value.</param>
    /// <exception cref="QPackException">Thrown when the entry cannot fit the table capacity.</exception>
    public void InsertWithLiteralName(string name, string value) => Insert(name, value);

    /// <summary>
    /// Inserts an entry whose name is taken from the static table
    /// (RFC 9204 §4.3.2, T = 1).
    /// </summary>
    /// <param name="staticIndex">The static-table index supplying the name.</param>
    /// <param name="value">The (Latin-1) field value.</param>
    /// <exception cref="QPackException">Thrown when the index is out of range or the entry cannot fit.</exception>
    public void InsertWithStaticNameReference(int staticIndex, string value)
    {
        if (!QPackStaticTable.TryGet(staticIndex, out string name, out _))
        {
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                $"QPACK insert with name reference targets static index {staticIndex}, which is out of range.");
        }

        Insert(name, value);
    }

    /// <summary>
    /// Inserts an entry whose name is taken from an existing dynamic-table entry
    /// (RFC 9204 §4.3.2, T = 0). The relative index is relative to the most
    /// recent insertion (§3.2.5).
    /// </summary>
    /// <param name="relativeIndex">The relative index of the dynamic entry supplying the name.</param>
    /// <param name="value">The (Latin-1) field value.</param>
    /// <exception cref="QPackException">Thrown when the index is out of range or the entry cannot fit.</exception>
    public void InsertWithDynamicNameReference(long relativeIndex, string value)
    {
        // Resolve (copy) the name before Insert, which may evict the referenced
        // entry to make room (RFC 9204 §3.2.2).
        Entry source = GetByRelativeIndex(relativeIndex);
        Insert(source.Name, value);
    }

    /// <summary>
    /// Duplicates an existing dynamic-table entry (RFC 9204 §4.3.4), adding a
    /// copy as the newest entry. The relative index is relative to the most
    /// recent insertion (§3.2.5).
    /// </summary>
    /// <param name="relativeIndex">The relative index of the entry to duplicate.</param>
    /// <exception cref="QPackException">Thrown when the index is out of range or the copy cannot fit.</exception>
    public void Duplicate(long relativeIndex)
    {
        Entry source = GetByRelativeIndex(relativeIndex);
        Insert(source.Name, source.Value);
    }

    /// <summary>
    /// Resolves an entry by its absolute index (RFC 9204 §3.2.4).
    /// </summary>
    /// <param name="absoluteIndex">The absolute index.</param>
    /// <param name="name">The resolved name, or the empty string when out of range.</param>
    /// <param name="value">The resolved value, or the empty string when out of range.</param>
    /// <returns><see langword="true"/> when the entry is present (not yet evicted).</returns>
    public bool TryGetByAbsoluteIndex(long absoluteIndex, out string name, out string value)
    {
        long storageIndex = absoluteIndex - _droppedCount;

        if (storageIndex < 0 || storageIndex >= _entries.Count)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }

        Entry entry = _entries[(int)storageIndex];
        name = entry.Name;
        value = entry.Value;
        return true;
    }

    private Entry GetByRelativeIndex(long relativeIndex)
    {
        // Relative index (§3.2.5): 0 is the most recently inserted entry.
        long absoluteIndex = InsertCount - 1 - relativeIndex;

        if (relativeIndex < 0 || !TryGetByAbsoluteIndex(absoluteIndex, out string name, out string value))
        {
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                $"QPACK relative index {relativeIndex} does not resolve to a live dynamic-table entry.");
        }

        return new Entry(name, value, EntrySize(name, value));
    }

    private void Insert(string name, string value)
    {
        long entrySize = EntrySize(name, value);

        if (entrySize > _capacity)
        {
            // Cannot fit even in an empty table (RFC 9204 §3.2.2) — an encoder
            // that inserts such an entry has committed a stream error.
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                $"QPACK insert of size {entrySize} exceeds the dynamic table capacity {_capacity}.");
        }

        EvictToFit(entrySize);
        _entries.Add(new Entry(name, value, entrySize));
        _size += entrySize;
    }

    private void EvictToFit(long incoming)
    {
        // Evict from the draining end (oldest, lowest absolute index) until the
        // incoming entry fits (RFC 9204 §3.2.2). The decoder always has room to
        // evict because the encoder guarantees it never references an entry it
        // is about to evict.
        while (_entries.Count > 0 && _size + incoming > _capacity)
        {
            Entry evicted = _entries[0];
            _entries.RemoveAt(0);
            _size -= evicted.Size;
            _droppedCount++;
        }
    }

    private static long EntrySize(string name, string value) => name.Length + value.Length + EntryOverhead;

    private readonly struct Entry
    {
        public Entry(string name, string value, long size)
        {
            Name = name;
            Value = value;
            Size = size;
        }

        public string Name { get; }

        public string Value { get; }

        public long Size { get; }
    }
}
