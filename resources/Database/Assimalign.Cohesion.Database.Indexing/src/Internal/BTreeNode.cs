using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// Overlay over the body of a <c>PageType.Index</c> page: a sorted-directory node
/// layout for the B+Tree, page format <see cref="FormatVersion"/>.
/// </summary>
/// <remarks>
/// <para>Body layout (offsets relative to the page body):</para>
/// <code>
/// 0  : ushort magic              — "BT"; format 1 pages began with their kind byte (1 or 2) here
/// 2  : byte   format version     — <see cref="FormatVersion"/>
/// 3  : byte   kind (1 = leaf, 2 = internal)
/// 4  : ushort entryCount
/// 6  : ushort dataStart          — entry data grows downward from the body end
/// 8  : long   nextLeaf           — leaf sibling chain (-1 = none)
/// 16 : long   prevLeaf
/// 24 : long   leftmostChild      — internal nodes only (-1 otherwise)
/// 32 : ushort[entryCount]        — directory of entry offsets, in entry order
/// </code>
/// <para>
/// Leaf entry: <c>[u16 keyLen][key][u64 entryRef][u64 writer][u64 deleter]</c> — the
/// writer/deleter stamps carry MVCC visibility; deleter 0 means live. Entries are
/// ordered by their identity <c>(key, entryRef, writer)</c>
/// (<see cref="BTreeEntryOrder"/>); the deleter is not part of the order, so a
/// tombstone or its undo never moves an entry.
/// </para>
/// <para>
/// Internal entry: <c>[u16 keyLen | tiebreaker &lt;&lt; 14][key][u64 entryRef]?[u64 writer]?[i64 child]</c>.
/// The key and its tiebreaker attributes form the separator: a strict upper bound
/// for every entry left of it and a lower bound for every entry in its child (the
/// child to its right). The top two bits of the length field count the tiebreaker
/// attributes the separator keeps — 0 (key only), 1 (key and entry reference) or 2
/// (key, entry reference and writer) — and an attribute it does not keep reads as
/// minus infinity. A child is addressed by its <em>slot</em>: <c>-1</c> for the
/// leftmost child, <c>i</c> for the child to the right of separator <c>i</c>.
/// </para>
/// </remarks>
internal readonly ref struct BTreeNode
{
    private readonly Span<byte> _body;

    private const int magicOffset = 0;
    private const int versionOffset = 2;
    private const int kindOffset = 3;
    private const int countOffset = 4;
    private const int dataStartOffset = 6;
    private const int nextLeafOffset = 8;
    private const int prevLeafOffset = 16;
    private const int leftmostChildOffset = 24;
    private const int directoryOffset = 32;

    private const int keyLengthMask = 0x3FFF;
    private const int tiebreakerShift = 14;

    /// <summary>
    /// The two bytes every node of this layout begins with, "BT" in little-endian
    /// order. Format 1 nodes began with their kind byte, 1 or 2, so no format 1 page
    /// can carry it.
    /// </summary>
    internal const ushort Magic = 0x5442;

    /// <summary>
    /// The page format this layout implements. Format 1 (before #1194) ordered
    /// entries by key alone and had no magic or version bytes; format 2 orders them
    /// by <c>(key, entry reference, writer)</c> and stores the tiebreaker in
    /// separators.
    /// </summary>
    internal const byte FormatVersion = 2;

    internal const byte LeafKind = 1;
    internal const byte InternalKind = 2;

    /// <summary>
    /// The bytes one directory slot occupies.
    /// </summary>
    internal const int DirectorySlotSize = 2;

    /// <summary>
    /// The largest key accepted, chosen so a fresh node always holds several entries
    /// (split correctness requires at least two per node).
    /// </summary>
    internal const int MaxKeyLength = 1024;

    internal BTreeNode(Span<byte> body)
    {
        _body = body;
    }

    /// <summary>
    /// Gets the underlying body span (used to rebuild a node in place).
    /// </summary>
    internal Span<byte> Body => _body;

    internal static BTreeNode Initialize(Span<byte> body, byte kind)
    {
        body.Clear();
        var node = new BTreeNode(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body[magicOffset..], Magic);
        body[versionOffset] = FormatVersion;
        body[kindOffset] = kind;
        node.EntryCount = 0;
        node.DataStart = (ushort)body.Length;
        node.NextLeaf = -1;
        node.PrevLeaf = -1;
        node.LeftmostChild = -1;
        return node;
    }

    /// <summary>
    /// Reads the page format a node body was written in: <see cref="FormatVersion"/>
    /// or another version behind the magic, <c>1</c> for a format 1 node (no magic;
    /// its first byte is its kind), and <c>0</c> when the body is no B-tree node of
    /// any format.
    /// </summary>
    internal static int ReadFormatVersion(ReadOnlySpan<byte> body)
    {
        if (body.Length <= directoryOffset)
        {
            return 0;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(body[magicOffset..]) == Magic)
        {
            return body[versionOffset];
        }

        return body[0] is LeafKind or InternalKind ? 1 : 0;
    }

    /// <summary>
    /// Gets a value indicating whether the body is a node of this layout: the magic,
    /// <see cref="FormatVersion"/>, and a leaf or internal kind.
    /// </summary>
    internal static bool IsCurrentFormat(ReadOnlySpan<byte> body)
        => body.Length > directoryOffset
            && BinaryPrimitives.ReadUInt16LittleEndian(body[magicOffset..]) == Magic
            && body[versionOffset] == FormatVersion
            && body[kindOffset] is LeafKind or InternalKind;

    internal byte Kind => _body[kindOffset];

    internal bool IsLeaf => Kind == LeafKind;

    internal ushort EntryCount
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_body[countOffset..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(_body[countOffset..], value);
    }

    internal ushort DataStart
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_body[dataStartOffset..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(_body[dataStartOffset..], value);
    }

    internal long NextLeaf
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(_body[nextLeafOffset..]);
        set => BinaryPrimitives.WriteInt64LittleEndian(_body[nextLeafOffset..], value);
    }

    internal long PrevLeaf
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(_body[prevLeafOffset..]);
        set => BinaryPrimitives.WriteInt64LittleEndian(_body[prevLeafOffset..], value);
    }

    internal long LeftmostChild
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(_body[leftmostChildOffset..]);
        set => BinaryPrimitives.WriteInt64LittleEndian(_body[leftmostChildOffset..], value);
    }

    /// <summary>
    /// Gets the bytes available for one more entry: the gap between the directory
    /// (with a slot reserved for that entry) and the entry data.
    /// </summary>
    internal int FreeSpace => DataStart - (directoryOffset + DirectorySlotSize * (EntryCount + 1));

    /// <summary>
    /// Gets the entry-data bytes no directory slot references any more — space
    /// <see cref="RemoveLeafEntry"/> leaves behind, recoverable by rebuilding the
    /// node. Linear in the entry count; meant for the full-node path only.
    /// </summary>
    internal int OrphanedLeafBytes
    {
        get
        {
            int referenced = 0;
            for (int index = 0; index < EntryCount; index++)
            {
                referenced += LeafEntrySize(GetKey(index).Length);
            }

            return _body.Length - DataStart - referenced;
        }
    }

    private ushort GetEntryOffset(int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(_body[(directoryOffset + DirectorySlotSize * index)..]);

    private ushort GetLengthField(int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(_body[offset..]);

    /// <summary>
    /// Gets the key bytes of entry <paramref name="index"/>: a leaf entry's key, or an
    /// internal entry's separator key (which suffix truncation may have shortened).
    /// </summary>
    internal ReadOnlySpan<byte> GetKey(int index)
    {
        int offset = GetEntryOffset(index);
        int keyLength = GetLengthField(offset) & keyLengthMask;
        return _body.Slice(offset + 2, keyLength);
    }

    private int GetValueOffset(int index)
    {
        int offset = GetEntryOffset(index);
        int keyLength = GetLengthField(offset) & keyLengthMask;
        return offset + 2 + keyLength;
    }

    internal ulong GetEntryReference(int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(_body[GetValueOffset(index)..]);

    internal ulong GetWriter(int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(_body[(GetValueOffset(index) + 8)..]);

    internal ulong GetDeleter(int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(_body[(GetValueOffset(index) + 16)..]);

    internal void SetDeleter(int index, ulong deleter)
        => BinaryPrimitives.WriteUInt64LittleEndian(_body[(GetValueOffset(index) + 16)..], deleter);

    /// <summary>
    /// Gets how many tiebreaker attributes internal entry <paramref name="index"/>
    /// keeps after its key (see <see cref="BTreeTiebreaker"/>).
    /// </summary>
    internal BTreeTiebreaker GetSeparatorTiebreaker(int index)
        => (BTreeTiebreaker)(GetLengthField(GetEntryOffset(index)) >> tiebreakerShift);

    /// <summary>
    /// Gets internal entry <paramref name="index"/>'s separator: its key and the
    /// tiebreaker attributes it keeps.
    /// </summary>
    internal BTreeSearchKey GetSeparator(int index)
    {
        int offset = GetEntryOffset(index);
        int field = GetLengthField(offset);
        int keyLength = field & keyLengthMask;
        var tiebreaker = (BTreeTiebreaker)(field >> tiebreakerShift);
        var key = _body.Slice(offset + 2, keyLength);
        int value = offset + 2 + keyLength;

        return tiebreaker switch
        {
            BTreeTiebreaker.Reference => BTreeSearchKey.AtReference(key, BinaryPrimitives.ReadUInt64LittleEndian(_body[value..])),
            BTreeTiebreaker.Entry => BTreeSearchKey.AtEntry(
                key, BinaryPrimitives.ReadUInt64LittleEndian(_body[value..]), BinaryPrimitives.ReadUInt64LittleEndian(_body[(value + 8)..])),
            _ => BTreeSearchKey.AtKey(key),
        };
    }

    /// <summary>
    /// Gets the child page of internal entry <paramref name="index"/>: the page to
    /// the right of its separator.
    /// </summary>
    internal long GetChild(int index)
    {
        int offset = GetEntryOffset(index);
        int field = GetLengthField(offset);
        int tiebreakerBytes = TiebreakerSize((BTreeTiebreaker)(field >> tiebreakerShift));
        return BinaryPrimitives.ReadInt64LittleEndian(_body[(offset + 2 + (field & keyLengthMask) + tiebreakerBytes)..]);
    }

    /// <summary>
    /// Compares <paramref name="key"/> with leaf entry <paramref name="index"/>'s
    /// identity <c>(key, entry reference, writer)</c>.
    /// </summary>
    internal int CompareToEntry(in BTreeSearchKey key, int index)
    {
        int offset = GetEntryOffset(index);
        int keyLength = GetLengthField(offset) & keyLengthMask;
        int comparison = key.Key.SequenceCompareTo(_body.Slice(offset + 2, keyLength));

        if (comparison != 0)
        {
            return comparison;
        }

        int value = offset + 2 + keyLength;
        return BTreeEntryOrder.CompareTiebreakers(
            key,
            BTreeTiebreaker.Entry,
            BinaryPrimitives.ReadUInt64LittleEndian(_body[value..]),
            BinaryPrimitives.ReadUInt64LittleEndian(_body[(value + 8)..]));
    }

    /// <summary>
    /// Compares <paramref name="key"/> with internal entry <paramref name="index"/>'s
    /// separator.
    /// </summary>
    internal int CompareToSeparator(in BTreeSearchKey key, int index)
    {
        int offset = GetEntryOffset(index);
        int field = GetLengthField(offset);
        int keyLength = field & keyLengthMask;
        int comparison = key.Key.SequenceCompareTo(_body.Slice(offset + 2, keyLength));

        if (comparison != 0)
        {
            return comparison;
        }

        var tiebreaker = (BTreeTiebreaker)(field >> tiebreakerShift);
        int value = offset + 2 + keyLength;
        return BTreeEntryOrder.CompareTiebreakers(
            key,
            tiebreaker,
            tiebreaker >= BTreeTiebreaker.Reference ? BinaryPrimitives.ReadUInt64LittleEndian(_body[value..]) : 0,
            tiebreaker == BTreeTiebreaker.Entry ? BinaryPrimitives.ReadUInt64LittleEndian(_body[(value + 8)..]) : 0);
    }

    /// <summary>
    /// Finds the lower bound on a leaf: the first entry not less than
    /// <paramref name="key"/>; <see cref="EntryCount"/> when every entry is smaller.
    /// </summary>
    internal int FindLowerBound(in BTreeSearchKey key)
    {
        int low = 0;
        int high = EntryCount;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (CompareToEntry(key, mid) > 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>
    /// Resolves the slot of the child that holds <paramref name="key"/> on an
    /// internal node: the child of the last separator not greater than the key
    /// (<c>-1</c>, the leftmost child, when every separator is greater). A separator
    /// is a lower bound of its child and a strict upper bound of everything to its
    /// left, so this is the only child whose range admits the key: an entry equal to
    /// the key is in it, and an entry greater than the key is in it or to its right.
    /// </summary>
    internal int FindChildSlot(in BTreeSearchKey key)
    {
        int low = 0;
        int high = EntryCount;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (CompareToSeparator(key, mid) >= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low - 1;
    }

    /// <summary>
    /// Gets the child page in <paramref name="slot"/>: <c>-1</c> is the leftmost
    /// child, <c>i</c> the child to the right of separator <c>i</c>.
    /// </summary>
    internal long GetChildAt(int slot) => slot < 0 ? LeftmostChild : GetChild(slot);

    internal static int LeafEntrySize(int keyLength) => 2 + keyLength + 8 + 8 + 8;

    /// <summary>
    /// Gets the bytes an internal entry takes: the length field, the separator key,
    /// the tiebreaker attributes it keeps, and the child pointer.
    /// </summary>
    internal static int InternalEntrySize(int keyLength, BTreeTiebreaker tiebreaker)
        => 2 + keyLength + TiebreakerSize(tiebreaker) + 8;

    /// <summary>
    /// Gets the bytes internal entry <paramref name="index"/> occupies, directory
    /// slot included.
    /// </summary>
    internal int InternalEntryFootprint(int index)
        => InternalEntrySize(GetKey(index).Length, GetSeparatorTiebreaker(index)) + DirectorySlotSize;

    private static int TiebreakerSize(BTreeTiebreaker tiebreaker) => tiebreaker switch
    {
        BTreeTiebreaker.Reference => 8,
        BTreeTiebreaker.Entry => 16,
        _ => 0,
    };

    /// <summary>
    /// Inserts a leaf entry at the given directory position. The caller has verified
    /// free space and the position's order.
    /// </summary>
    internal void InsertLeafEntry(int index, ReadOnlySpan<byte> key, ulong entryReference, ulong writer, ulong deleter)
    {
        int size = LeafEntrySize(key.Length);
        int offset = DataStart - size;

        BinaryPrimitives.WriteUInt16LittleEndian(_body[offset..], (ushort)key.Length);
        key.CopyTo(_body[(offset + 2)..]);
        BinaryPrimitives.WriteUInt64LittleEndian(_body[(offset + 2 + key.Length)..], entryReference);
        BinaryPrimitives.WriteUInt64LittleEndian(_body[(offset + 2 + key.Length + 8)..], writer);
        BinaryPrimitives.WriteUInt64LittleEndian(_body[(offset + 2 + key.Length + 16)..], deleter);

        InsertDirectorySlot(index, (ushort)offset);
        DataStart = (ushort)offset;
    }

    /// <summary>
    /// Inserts an internal entry (separator + right child) at the given directory
    /// position. The caller has verified free space and the separator's order.
    /// </summary>
    internal void InsertInternalEntry(int index, in BTreeSearchKey separator, long child)
    {
        var tiebreaker = separator.Tiebreaker;
        int keyLength = separator.Key.Length;
        int offset = DataStart - InternalEntrySize(keyLength, tiebreaker);
        int value = offset + 2 + keyLength;

        BinaryPrimitives.WriteUInt16LittleEndian(_body[offset..], (ushort)(keyLength | ((int)tiebreaker << tiebreakerShift)));
        separator.Key.CopyTo(_body[(offset + 2)..]);

        if (tiebreaker >= BTreeTiebreaker.Reference)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_body[value..], separator.EntryReference);
            value += 8;
        }

        if (tiebreaker == BTreeTiebreaker.Entry)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_body[value..], separator.Writer);
            value += 8;
        }

        BinaryPrimitives.WriteInt64LittleEndian(_body[value..], child);

        InsertDirectorySlot(index, (ushort)offset);
        DataStart = (ushort)offset;
    }

    private void InsertDirectorySlot(int index, ushort offset)
    {
        int count = EntryCount;
        int start = directoryOffset + DirectorySlotSize * index;

        _body.Slice(start, DirectorySlotSize * (count - index)).CopyTo(_body[(start + DirectorySlotSize)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(_body[start..], offset);
        EntryCount = (ushort)(count + 1);
    }

    /// <summary>
    /// Removes a leaf entry from the sorted directory. The entry's data bytes stay
    /// orphaned in the body until the node is rebuilt (a split, or the compaction an
    /// insert performs when only the orphaned bytes stand between it and a split) or
    /// vacuumed — the removal exists for the rare undo paths (aborted-writer purge),
    /// where the bounded space cost beats a full node rewrite per removed entry.
    /// </summary>
    internal void RemoveLeafEntry(int index)
    {
        int count = EntryCount;
        int start = directoryOffset + DirectorySlotSize * index;

        _body.Slice(start + DirectorySlotSize, DirectorySlotSize * (count - index - 1)).CopyTo(_body[start..]);
        EntryCount = (ushort)(count - 1);
    }
}
