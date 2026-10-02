using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Creates <see cref="IIndexManager"/> instances backed by B+Trees on shared
/// storage pages.
/// </summary>
public static class BTreeIndexManager
{
    /// <summary>
    /// Gets the B-tree page format this engine writes and the only one it reads.
    /// Format 2 (#1194) orders entries by <c>(key, entry reference, writer)</c> and
    /// stamps a magic and this version on every node page; format 1 ordered entries by
    /// key alone and carried no stamp.
    /// </summary>
    /// <remarks>
    /// A property, not a constant: the value changes with every page-format change, and
    /// a constant would be compiled into separately built consumers, which would then
    /// report the format of the engine they were built against.
    /// </remarks>
    public static int FormatVersion => BTreeNode.FormatVersion;

    /// <summary>
    /// Creates an index manager over the specified storage, attaching the trees in
    /// <see cref="BTreeIndexManagerOptions.ExistingIndexes"/>.
    /// </summary>
    /// <param name="options">The composition options.</param>
    /// <returns>The index manager (it also implements <see cref="IIndexRegistry"/> for catalog persistence).</returns>
    /// <exception cref="IndexFormatException">
    /// An existing tree's root page is not in page format <see cref="FormatVersion"/>;
    /// no tree is attached.
    /// </exception>
    public static IIndexManager Create(BTreeIndexManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DefaultIndexManager(options);
    }

    /// <summary>
    /// Checks that every tree in <paramref name="registrations"/> is in page format
    /// <see cref="FormatVersion"/> — the check <see cref="Create"/> makes when it
    /// attaches existing trees, on its own. It reads each tree's root page and writes
    /// nothing, so a model whose open recovers its record space before it attaches its
    /// indexes calls it first, and refuses a database it cannot read before recovery
    /// writes to it.
    /// </summary>
    /// <param name="storage">The storage whose pages hold the trees.</param>
    /// <param name="registrations">The registrations of the trees to check.</param>
    /// <exception cref="IndexFormatException">A tree's root page is not in page format <see cref="FormatVersion"/>.</exception>
    public static void EnsureFormat(IStorage storage, IEnumerable<BTreeIndexRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            BTreeIndex.EnsureFormat(storage, registration);
        }
    }
}
