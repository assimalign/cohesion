using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// A forward-only cursor over the visible entries of an index scan.
/// </summary>
public interface IIndexCursor : IAsyncDisposable
{
    /// <summary>
    /// Gets the key at the current position.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before the first <see cref="MoveNextAsync"/> or after the scan is exhausted.</exception>
    IndexKey CurrentKey { get; }

    /// <summary>
    /// Gets the entry reference at the current position.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before the first <see cref="MoveNextAsync"/> or after the scan is exhausted.</exception>
    ulong CurrentEntryReference { get; }

    /// <summary>
    /// Advances the cursor to the next visible entry.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the cursor advanced; false when the scan is exhausted.</returns>
    ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default);
}
