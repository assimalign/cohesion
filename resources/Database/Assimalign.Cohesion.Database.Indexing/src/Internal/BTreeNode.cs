using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Overlay over the body of a <c>PageType.Index</c> page: a sorted-directory node
/// layout for the B+Tree.
/// </summary>
/// <remarks>
/// <para>Body layout (offsets relative to the page body):</para>
/// <code>
/// 0  : byte   kind (1 = leaf, 2 = internal)
/// 1  : ushort entryCount
/// 3  : ushort dataStart          — entry data grows downward from the body end
/// 5  : long   nextLeaf           — leaf sibling chain (-1 = none)
/// 13 : long   prevLeaf
/// 21 : long   leftmostChild      — internal nodes only (-1 otherwise)
/// 29 : ushort[entryCount]        — directory of entry offsets, sorted by key
/// </code>
/// <para>
/// Leaf entry: <c>[u16 keyLen][key][u64 entryRef][u64 writer][u64 deleter]</c> —
/// the writer/deleter stamps carry MVCC visibility; deleter 0 means live.
/// Internal entry: <c>[u16 keyLen][key][i64 child]</c> where the key is the
/// minimum key of the child subtree to its right.
/// </para>
/// </remarks>
internal readonly ref struct BTreeNode
{
    private readonly Span<byte> _body;

    private const int kindOffset = 0;
    private const int countOffset = 1;
    private const int dataStartOffset = 3;
    private const int nextLeafOffset = 5;
    private const int prevLeafOffset = 13;
    private const int leftmostChildOffset = 21;
    private const int directoryOffset = 29;

    internal const byte LeafKind = 1;
    internal const byte InternalKind = 2;

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
        body[kindOffset] = kind;
        node.EntryCount = 0;
        node.DataStart = (ushort)body.Length;
        node.NextLeaf = -1;
        node.PrevLeaf = -1;
        node.LeftmostChild = -1;
        return node;
    }

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

    internal int FreeSpace => DataStart - (directoryOffset + 2 * (EntryCount + 1));

    private ushort GetEntryOffset(int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(_body[(directoryOffset + 2 * index)..]);

    internal ReadOnlySpan<byte> GetKey(int index)
    {
        int offset = GetEntryOffset(index);
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(_body[offset..]);
        return _body.Slice(offset + 2, keyLength);
    }

    private int GetValueOffset(int index)
    {
        int offset = GetEntryOffset(index);
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(_body[offset..]);
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

    internal long GetChild(int index)
        => BinaryPrimitives.ReadInt64LittleEndian(_body[GetValueOffset(index)..]);

    /// <summary>
    /// Finds the lower bound: the first directory index whose key is greater than or
    /// equal to <paramref name="key"/>; <see cref="EntryCount"/> when every key is smaller.
    /// </summary>
    internal int FindLowerBound(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = EntryCount;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (GetKey(mid).SequenceCompareTo(key) < 0)
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
    /// Resolves the child page to descend into for <paramref name="key"/> on an
    /// internal node: the child of the greatest separator not exceeding the key,
    /// or the leftmost child when the key precedes every separator.
    /// </summary>
    internal long FindChild(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = EntryCount;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (GetKey(mid).SequenceCompareTo(key) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low == 0 ? LeftmostChild : GetChild(low - 1);
    }

    internal int LeafEntrySize(int keyLength) => 2 + keyLength + 8 + 8 + 8;

    internal int InternalEntrySize(int keyLength) => 2 + keyLength + 8;

    /// <summary>
    /// Inserts a leaf entry at the given directory position. The caller has verified
    /// free space.
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
    /// Inserts an internal entry (separator + right child) at the given directory position.
    /// </summary>
    internal void InsertInternalEntry(int index, ReadOnlySpan<byte> key, long child)
    {
        int size = InternalEntrySize(key.Length);
        int offset = DataStart - size;

        BinaryPrimitives.WriteUInt16LittleEndian(_body[offset..], (ushort)key.Length);
        key.CopyTo(_body[(offset + 2)..]);
        BinaryPrimitives.WriteInt64LittleEndian(_body[(offset + 2 + key.Length)..], child);

        InsertDirectorySlot(index, (ushort)offset);
        DataStart = (ushort)offset;
    }

    private void InsertDirectorySlot(int index, ushort offset)
    {
        int count = EntryCount;
        int start = directoryOffset + 2 * index;

        _body.Slice(start, 2 * (count - index)).CopyTo(_body[(start + 2)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(_body[start..], offset);
        EntryCount = (ushort)(count + 1);
    }
}
