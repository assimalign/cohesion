using System;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// The tree's one total order: <c>(key, entry reference, writer)</c>, compared
/// attribute by attribute — the key as unsigned bytes, then the reference and the
/// writer as unsigned integers — with a missing attribute reading as minus infinity
/// (PostgreSQL nbtree's heap-TID tiebreaker, <c>nbtsearch.c</c> <c>_bt_compare</c>).
/// Every structure decision — insert position, separators, descents, seeks, and the
/// entry lookups of delete, erase, clear-deleter and the unique check — compares
/// through this type.
/// </summary>
internal static class BTreeEntryOrder
{
    /// <summary>
    /// Compares the tiebreaker attributes of <paramref name="left"/> with
    /// <c>(entryReference, writer)</c>, of which <paramref name="tiebreaker"/> says how
    /// many are present — the comparison once two keys are equal (the nodes compare
    /// key bytes first, <see cref="BTreeNode.CompareToEntry"/> and
    /// <see cref="BTreeNode.CompareToSeparator"/>).
    /// </summary>
    internal static int CompareTiebreakers(in BTreeSearchKey left, BTreeTiebreaker tiebreaker, ulong entryReference, ulong writer)
    {
        int comparison;
        var leftTiebreaker = left.Tiebreaker;

        if (leftTiebreaker == BTreeTiebreaker.PlusInfinity || tiebreaker == BTreeTiebreaker.PlusInfinity)
        {
            return leftTiebreaker == tiebreaker ? 0 : leftTiebreaker == BTreeTiebreaker.PlusInfinity ? 1 : -1;
        }

        // The entry reference: a side without it holds minus infinity there.
        if (leftTiebreaker == BTreeTiebreaker.None || tiebreaker == BTreeTiebreaker.None)
        {
            return ((int)leftTiebreaker == 0 ? 0 : 1) - ((int)tiebreaker == 0 ? 0 : 1);
        }

        comparison = left.EntryReference.CompareTo(entryReference);
        if (comparison != 0)
        {
            return comparison;
        }

        // The writer, likewise.
        if (leftTiebreaker == BTreeTiebreaker.Reference || tiebreaker == BTreeTiebreaker.Reference)
        {
            return (leftTiebreaker == BTreeTiebreaker.Reference ? 0 : 1) - (tiebreaker == BTreeTiebreaker.Reference ? 0 : 1);
        }

        return left.Writer.CompareTo(writer);
    }

    /// <summary>
    /// Builds the separator for a split between <paramref name="lastLeft"/> and
    /// <paramref name="firstRight"/>, two adjacent leaf entries with
    /// <c>lastLeft &lt; firstRight</c>: the shortest prefix of
    /// <paramref name="firstRight"/> that is greater than <paramref name="lastLeft"/>.
    /// The result <c>s</c> satisfies <c>lastLeft &lt; s &lt;= firstRight</c>, so it
    /// is a strict upper bound of the left half and a lower bound of the right one.
    /// </summary>
    /// <remarks>
    /// Truncation follows PostgreSQL's (<c>nbtutils.c</c> <c>_bt_truncate</c> and
    /// <c>_bt_keep_natts</c>): attributes after the first that distinguishes the two
    /// entries are dropped and read as minus infinity. When the keys differ, the
    /// separator keeps no tiebreaker and only the key bytes up to and including the
    /// first byte that differs — PostgreSQL truncates at whole-attribute granularity,
    /// but an <see cref="IndexKey"/> is one unsigned byte string, so byte-granular
    /// truncation is the Bayer–Unterauer simple prefix B-tree its README describes.
    /// When the keys are equal it keeps the reference, and the writer only when the
    /// references are equal too.
    /// </remarks>
    internal static Separator BuildSeparator(in BTreeNode leaf, int lastLeft, int firstRight)
    {
        var leftKey = leaf.GetKey(lastLeft);
        var rightKey = leaf.GetKey(firstRight);
        int common = leftKey.CommonPrefixLength(rightKey);

        if (common < rightKey.Length && (common == leftKey.Length || leftKey[common] != rightKey[common]))
        {
            // The keys differ, first at byte `common` (or the left key ends there):
            // that byte decides, and nothing after it is needed.
            return new Separator(rightKey[..(common + 1)].ToArray(), BTreeTiebreaker.None, 0, 0);
        }

        ulong rightReference = leaf.GetEntryReference(firstRight);
        if (leaf.GetEntryReference(lastLeft) != rightReference)
        {
            return new Separator(rightKey.ToArray(), BTreeTiebreaker.Reference, rightReference, 0);
        }

        return new Separator(rightKey.ToArray(), BTreeTiebreaker.Entry, rightReference, leaf.GetWriter(firstRight));
    }

    /// <summary>
    /// A separator held outside a page, while a split moves it between nodes.
    /// </summary>
    internal sealed record Separator(byte[] Key, BTreeTiebreaker Tiebreaker, ulong EntryReference, ulong Writer)
    {
        /// <summary>
        /// Gets the separator as a position in the order.
        /// </summary>
        internal BTreeSearchKey AsSearchKey() => Tiebreaker switch
        {
            BTreeTiebreaker.Reference => BTreeSearchKey.AtReference(Key, EntryReference),
            BTreeTiebreaker.Entry => BTreeSearchKey.AtEntry(Key, EntryReference, Writer),
            _ => BTreeSearchKey.AtKey(Key),
        };

        /// <summary>
        /// Copies internal entry <paramref name="index"/>'s separator out of its node.
        /// </summary>
        internal static Separator FromNode(in BTreeNode node, int index)
        {
            var separator = node.GetSeparator(index);
            return new Separator(separator.Key.ToArray(), separator.Tiebreaker, separator.EntryReference, separator.Writer);
        }
    }
}
