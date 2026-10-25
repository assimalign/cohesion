using System;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// A position in the tree's total order: a key, optionally followed by an entry
/// reference and a writer. Leaf entries always carry all three; separators keep as
/// many as they need; searches carry what they know.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("BTreeSearchKey")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Indexing.Internal.BTreeSearchKey.DebugView))]
internal readonly ref struct BTreeSearchKey
{
    private BTreeSearchKey(ReadOnlySpan<byte> key, BTreeTiebreaker tiebreaker, ulong entryReference, ulong writer)
    {
        Key = key;
        Tiebreaker = tiebreaker;
        EntryReference = entryReference;
        Writer = writer;
    }

    /// <summary>
    /// Gets the key bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Key { get; }

    /// <summary>
    /// Gets which tiebreaker attributes follow the key.
    /// </summary>
    internal BTreeTiebreaker Tiebreaker { get; }

    /// <summary>
    /// Gets the entry reference (meaningful from <see cref="BTreeTiebreaker.Reference"/> on).
    /// </summary>
    internal ulong EntryReference { get; }

    /// <summary>
    /// Gets the writer stamp (meaningful for <see cref="BTreeTiebreaker.Entry"/>).
    /// </summary>
    internal ulong Writer { get; }

    /// <summary>
    /// The position before every entry of <paramref name="key"/>: where an equality
    /// or inclusive range seek, and the unique check, start.
    /// </summary>
    internal static BTreeSearchKey AtKey(ReadOnlySpan<byte> key) => new(key, BTreeTiebreaker.None, 0, 0);

    /// <summary>
    /// The position after every entry of <paramref name="key"/>: where a range seek
    /// that excludes its start key starts.
    /// </summary>
    internal static BTreeSearchKey AfterKey(ReadOnlySpan<byte> key) => new(key, BTreeTiebreaker.PlusInfinity, 0, 0);

    /// <summary>
    /// The position before every entry of <paramref name="key"/> that maps to
    /// <paramref name="entryReference"/>: the versions a delete or a clear-deleter
    /// chooses among.
    /// </summary>
    internal static BTreeSearchKey AtReference(ReadOnlySpan<byte> key, ulong entryReference)
        => new(key, BTreeTiebreaker.Reference, entryReference, 0);

    /// <summary>
    /// The position after every entry of <paramref name="key"/> that maps to
    /// <paramref name="entryReference"/>: where a lookup that wants the reference's
    /// newest version starts and walks backward. Entry references are unsigned
    /// integers, so that position is <c>(key, entryReference + 1, -inf)</c>, or
    /// <c>(key, +inf)</c> for the largest reference; no other entry lies between it and
    /// the reference's last version.
    /// </summary>
    internal static BTreeSearchKey AfterReference(ReadOnlySpan<byte> key, ulong entryReference)
        => entryReference == ulong.MaxValue ? AfterKey(key) : AtReference(key, entryReference + 1);

    /// <summary>
    /// One entry's full identity: where an insert goes and what an erase removes.
    /// </summary>
    internal static BTreeSearchKey AtEntry(ReadOnlySpan<byte> key, ulong entryReference, ulong writer)
        => new(key, BTreeTiebreaker.Entry, entryReference, writer);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Indexing.Internal.BTreeSearchKey value)
        {
            _ = value;
        }
    }
}
