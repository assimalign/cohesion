using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Stores and resolves MVCC version chains for stored entries.
/// </summary>
/// <remarks>
/// <para>
/// Each logical entry (row, document, key) heads a chain of versions stamped with the writing
/// transaction's sequence. Readers resolve the newest chain member visible through their
/// snapshot; the transaction manager's <see cref="TransactionManager.OldestActive"/> bound drives
/// pruning of versions no snapshot can reach.
/// </para>
/// <para>
/// A variant set whose leaves all live in this assembly: the record-space store a
/// <see cref="TransactionCoordinator"/> composes over a database's storage
/// (<see cref="RecordSpaceVersionStore"/>), and the in-memory store <see cref="CreateInMemory"/>
/// returns. The constructor is therefore <c>private protected</c> (<c>database-area.md</c>,
/// rule 3): this assembly's test doubles derive through its test-only <c>InternalsVisibleTo</c>,
/// and no other assembly can. The public members are non-virtual; they check what every store
/// checks and call the protected cores.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class VersionStore
{
    /// <summary>
    /// Initializes a version store. Only this assembly (and its own tests) can derive.
    /// </summary>
    private protected VersionStore()
    {
    }

    /// <summary>
    /// Creates an in-memory version store: per-entry version chains resolved
    /// newest-first against snapshots. Model engines bring page-backed stores;
    /// this one serves embedded working state and tests.
    /// </summary>
    /// <returns>The version store.</returns>
    public static VersionStore CreateInMemory() => new InMemoryVersionStore();

    /// <summary>
    /// Appends a new version for the specified entry, stamped with the writing
    /// transaction's sequence.
    /// </summary>
    /// <param name="objectId">The identity of the containing object (table, collection, container).</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="payload">The version payload.</param>
    /// <param name="writer">The sequence of the writing transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the append.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled before the append.</exception>
    public ValueTask AppendVersionAsync(ulong objectId, ulong entryId, ReadOnlyMemory<byte> payload, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return AppendVersionCoreAsync(objectId, entryId, payload, writer, cancellationToken);
    }

    /// <summary>
    /// Resolves the newest version of the specified entry visible through the snapshot.
    /// </summary>
    /// <param name="objectId">The identity of the containing object.</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="snapshot">The snapshot visibility is resolved against.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The visible version payload, or null when no visible version exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before the read.</exception>
    public ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionAsync(ulong objectId, ulong entryId, TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return GetVisibleVersionCoreAsync(objectId, entryId, snapshot, cancellationToken);
    }

    /// <summary>
    /// Removes versions that no active or future snapshot can reach.
    /// </summary>
    /// <param name="oldestActive">The oldest transaction sequence still active.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of versions pruned.</returns>
    public ValueTask<long> PruneAsync(TransactionSequence oldestActive, CancellationToken cancellationToken = default)
        => PruneCoreAsync(oldestActive, cancellationToken);

    /// <summary>
    /// Removes every version written by the specified transaction. Called during
    /// rollback and recovery so aborted writers are never consulted by snapshots —
    /// the snapshot value object deliberately has no commit-log awareness (see the
    /// project design), which makes unlinking aborted versions the store's duty.
    /// </summary>
    /// <param name="writer">The sequence of the aborted transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of versions removed.</returns>
    public ValueTask<long> PurgeWriterAsync(TransactionSequence writer, CancellationToken cancellationToken = default)
        => PurgeWriterCoreAsync(writer, cancellationToken);

    /// <summary>
    /// Appends the version (see <see cref="AppendVersionAsync"/>); the token was not canceled on entry.
    /// </summary>
    /// <param name="objectId">The identity of the containing object.</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="payload">The version payload.</param>
    /// <param name="writer">The sequence of the writing transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the append.</returns>
    protected abstract ValueTask AppendVersionCoreAsync(ulong objectId, ulong entryId, ReadOnlyMemory<byte> payload, TransactionSequence writer, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the visible version (see <see cref="GetVisibleVersionAsync"/>); the snapshot is
    /// not null and the token was not canceled on entry.
    /// </summary>
    /// <param name="objectId">The identity of the containing object.</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="snapshot">The snapshot visibility is resolved against.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The visible version payload, or null when no visible version exists.</returns>
    protected abstract ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionCoreAsync(ulong objectId, ulong entryId, TransactionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Prunes unreachable versions (see <see cref="PruneAsync"/>).
    /// </summary>
    /// <param name="oldestActive">The oldest transaction sequence still active.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of versions pruned.</returns>
    protected abstract ValueTask<long> PruneCoreAsync(TransactionSequence oldestActive, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an aborted writer's versions (see <see cref="PurgeWriterAsync"/>).
    /// </summary>
    /// <param name="writer">The sequence of the aborted transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of versions removed.</returns>
    protected abstract ValueTask<long> PurgeWriterCoreAsync(TransactionSequence writer, CancellationToken cancellationToken);
}
