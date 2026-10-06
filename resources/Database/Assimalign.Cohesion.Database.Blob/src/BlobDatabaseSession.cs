using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// A blob session: runs its own container operations, and the blob operations and streams of the
/// containers it returns, one at a time, in autocommit or in its explicit transaction. A blob
/// session has no query language.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's state, its explicit transaction and the "already active" check are the root
/// base's</b> (<see cref="DatabaseSession"/>, §6.4 of the concrete-types plan): BEGIN is refused
/// with "A transaction or operation is already active on this session." while the session's
/// transaction is usable, while another BEGIN runs, or while an operation (an open blob stream
/// included) holds the session; a closed session refuses BEGIN and both execute seams with "The
/// session is closed.", while its container operations and the operations of a container bound to
/// it check the database first (disposed, then offline) and then report the closed session with
/// the same message; and disposal ends the running operation, then rolls the open transaction back
/// as the session's teardown. This type supplies the model's work: the isolation-level and offline
/// refusals of BEGIN, the container operations, and the translation of the kernel's exceptions at
/// the model boundary.
/// </para>
/// <para>
/// <b>A failed operation aborts the explicit transaction (#1225, the contract #1188 set for
/// Graph).</b> The transaction stays the session's transaction until the caller commits, rolls back
/// or disposes it; the session then refuses every operation and BEGIN with <c>COHDBB001</c> until
/// the caller rolls back. A failure never turns later operations into autocommit writes. An
/// operation (a container operation of the session, or a blob operation of a
/// <see cref="BlobContainer"/> bound to the session) holds the session (the base's operation hold)
/// from its start to its end, which for a blob stream is the stream's disposal, so a second
/// operation on the session is refused meanwhile, and so is a BEGIN.
/// </para>
/// <para>
/// <b>Option B (concrete-types plan, §6.6).</b> The session exposes its container operations
/// itself (<see cref="CreateContainerAsync"/>, <see cref="GetContainerAsync"/>,
/// <see cref="DropContainerAsync"/>, <see cref="GetContainersAsync"/>), and
/// <see cref="Database"/> is the unbound <see cref="BlobDatabase"/>, whose own container
/// operations run in autocommit outside any session. Disposing the session closes the session;
/// disposing its database closes the database for every session, and the engine refuses to reopen
/// it (<see cref="ObjectDisposedException"/>) until it is dropped or the engine is recreated.
/// Before phase 4 the session's database was a session-bound view whose disposal closed the
/// session, so <c>Dispose</c> meant two things on one type.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; <see cref="BlobDatabase.CreateSessionAsync"/> creates it. The database
/// and the transaction are re-exposed typed with <c>new</c> members over the base's public
/// members.
/// </para>
/// </remarks>
public sealed class BlobDatabaseSession : DatabaseSession
{
    private readonly BlobDatabase _database;
    private readonly object _sync = new();

    // The operation running on the session, which the session's teardown aborts; operations run
    // one at a time, so there is at most one.
    private BlobOperation? _operation;

    internal BlobDatabaseSession(BlobDatabase database)
        : base(database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the blob database this session is scoped to: the unbound database, whose disposal
    /// closes the database, not the session (option B, §6.6 of the concrete-types plan).
    /// </summary>
    /// <remarks>
    /// The database's own container operations, and the operations of a container it returns, run
    /// in autocommit, never in this session's transaction. A write through it
    /// (<see cref="BlobDatabase.CreateContainerAsync(string, CancellationToken)"/>,
    /// <see cref="BlobDatabase.DropContainerAsync(string, CancellationToken)"/>, a blob upload or
    /// delete of a container it returned) while the session's explicit transaction has written
    /// waits for that transaction's writer lock (the engine has one writer at a time) until the
    /// transaction ends or the call's token is canceled; inside a transaction, use the session's
    /// own container operations (<see cref="CreateContainerAsync"/>, <see cref="GetContainerAsync"/>,
    /// <see cref="DropContainerAsync"/>).
    /// </remarks>
    public new BlobDatabase Database => _database;

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including an aborted
    /// transaction (<see cref="TransactionState.Faulted"/>) that still waits for the caller's
    /// rollback; null when none is open.
    /// </summary>
    public new BlobDatabaseTransaction? CurrentTransaction => (BlobDatabaseTransaction?)base.CurrentTransaction;

    /// <summary>
    /// Begins an explicit transaction at the default isolation level, <see cref="IsolationLevel.Snapshot"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="ObjectDisposedException">
    /// The database has been disposed (dropped, closed, or its engine disposed) and the session is
    /// still open; a closed session is refused as closed first.
    /// </exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; or the session's
    /// transaction refuses work (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    public new async ValueTask<BlobDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => (BlobDatabaseTransaction)await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Begins an explicit transaction at the requested isolation level.
    /// </summary>
    /// <param name="isolationLevel">
    /// The isolation level the transaction executes under: <see cref="IsolationLevel.Snapshot"/> or
    /// <see cref="IsolationLevel.ReadCommitted"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="ObjectDisposedException">
    /// The database has been disposed (dropped, closed, or its engine disposed) and the session is
    /// still open; a closed session is refused as closed first.
    /// </exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; the session's
    /// transaction refuses work (<c>COHDBB001</c>); or the blob engine does not support
    /// <paramref name="isolationLevel"/>.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <remarks>
    /// The base refuses a closed session and an active transaction or operation before the
    /// isolation level, the disposed database and the offline database are checked.
    /// </remarks>
    public new async ValueTask<BlobDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => (BlobDatabaseTransaction)await base.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new blob container as an operation of this session: in its explicit transaction
    /// when one is open, otherwise in autocommit.
    /// </summary>
    /// <param name="name">The name of the container to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created container, bound to this session.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// A container with the same name already exists; the session is closed, another operation
    /// holds it, or its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The blob catalog changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit operation's commit record could not be confirmed durable.</exception>
    public ValueTask<BlobContainer> CreateContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.CreateContainerAsync(name, this, cancellationToken);

    /// <summary>
    /// Opens an existing blob container as an operation of this session.
    /// </summary>
    /// <param name="name">The name of the container to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened container, bound to this session.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The container does not exist in the operation's snapshot; the session is closed, another
    /// operation holds it, or its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit operation's commit record could not be confirmed durable.</exception>
    public ValueTask<BlobContainer> GetContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.GetContainerAsync(name, this, cancellationToken);

    /// <summary>
    /// Drops a blob container and its blobs as an operation of this session.
    /// </summary>
    /// <param name="name">The name of the container to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the container is dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseObjectLockedException">The container is owned by a schema.</exception>
    /// <exception cref="DatabaseException">
    /// The container does not exist; the session is closed, another operation holds it, or its
    /// transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The container or its blobs changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit operation's commit record could not be confirmed durable.</exception>
    public ValueTask DropContainerAsync(string name, CancellationToken cancellationToken = default)
        => _database.DropContainerAsync(name, this, cancellationToken);

    /// <summary>
    /// Enumerates the containers of the database as one operation of this session, run when the
    /// enumeration starts; the containers are then yielded.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of the containers, bound to this session, in ordinal name order.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed (also while the containers are yielded).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed (also while the containers are yielded), another operation holds it, or
    /// its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit operation's commit record could not be confirmed durable.</exception>
    public IAsyncEnumerable<BlobContainer> GetContainersAsync(CancellationToken cancellationToken = default)
        => _database.GetContainersAsync(this, cancellationToken);

    /// <summary>
    /// Holds the session for one operation and admits it into the explicit transaction, if one is
    /// open: the operation's start. The operation releases both through
    /// <see cref="ReleaseOperation"/>.
    /// </summary>
    /// <returns>The explicit transaction the operation runs in, or null for autocommit.</returns>
    /// <exception cref="DatabaseException">
    /// The session is closed; another operation or a BEGIN holds it; or its transaction refuses
    /// operations (<c>COHDBB001</c>).
    /// </exception>
    internal BlobDatabaseTransaction? EnterOperation()
    {
        if (!TryEnterOperation())
        {
            throw new DatabaseException("Dispose the active blob stream before starting another operation on this session.");
        }

        var transaction = CurrentTransaction;
        if (transaction is null || transaction.TryBeginBlobOperation())
        {
            return transaction;
        }

        ExitOperation();
        throw transaction.CreateOperationRefusal();
    }

    /// <summary>
    /// Records the operation that holds the session, so the session's teardown aborts it.
    /// </summary>
    /// <param name="operation">The running operation.</param>
    /// <exception cref="DatabaseException">The session closed while the operation started.</exception>
    internal void Track(BlobOperation operation)
    {
        // The check and the write share the lock the teardown reads under, after it marked the
        // session closed: either the teardown sees the operation, or the operation sees the close.
        lock (_sync)
        {
            ThrowIfClosed();
            _operation = operation;
        }
    }

    /// <summary>
    /// Ends an operation <see cref="EnterOperation"/> started: its admission into the transaction,
    /// if any, and its hold on the session.
    /// </summary>
    /// <param name="transaction">The explicit transaction the operation ran in, or null.</param>
    internal void ReleaseOperation(BlobDatabaseTransaction? transaction)
    {
        transaction?.EndBlobOperation();
        lock (_sync)
        {
            _operation = null;
        }

        ExitOperation();
    }

    /// <summary>
    /// Throws once the database has been disposed or the session is closed ("The session is
    /// closed.").
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="DatabaseException">The session is closed.</exception>
    internal void ThrowIfNotOpen()
    {
        _database.EnsureNotDisposed();
        ThrowIfClosed();
    }

    /// <summary>
    /// Aborts the session's explicit transaction for a failure that ends the session, unless an
    /// operation already aborted it. The wire server calls it before it reports a terminal failure,
    /// so a failure that came before an operation started (which leaves an in-process transaction
    /// unchanged) has aborted a host-opened transaction before the client sees the error, and the
    /// host's commit fails with <c>COHDBB001</c> naming it whichever of the commit and the
    /// connection's teardown runs first. The server's pump runs one exchange at a time and its
    /// operations have ended by then, so no operation of the session races the abort.
    /// </summary>
    /// <param name="cause">The failure the client is told about.</param>
    /// <returns>A task that completes once the transaction is aborted, or at once.</returns>
    internal async ValueTask AbortTransactionAsync(Exception cause)
    {
        if (CurrentTransaction is { AcceptsOperations: true } transaction)
        {
            await transaction.AbortForFailedOperationAsync(cause).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted))
        {
            throw new DatabaseException("The blob engine supports Snapshot and ReadCommitted isolation.");
        }

        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        TransactionContext context;
        try
        {
            context = await _database.Coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A begin that met the offline storage (#1243) gets the coded refusal.
            var reported = _database.TranslateOffline(error);
            if (ReferenceEquals(reported, error))
            {
                throw;
            }

            throw reported;
        }

        return new BlobDatabaseTransaction(_database.Coordinator, context, _database);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A blob session has no query language: every request is refused, after the database's
    /// disposal and offline checks, with a <see cref="DatabaseException"/> that names the
    /// session's container operations.
    /// </remarks>
    protected override ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        throw new DatabaseException("Blob sessions have no query language. Use the session's container operations.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// A blob session has no statement language: every statement is refused, after the database's
    /// disposal and offline checks, with a <see cref="DatabaseException"/>.
    /// </remarks>
    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        throw new DatabaseException("Blob sessions have no statement language or server-scoped commands.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Aborts the operation running on the session, if any (an open blob stream included), with
    /// "The blob session closed while the operation was running."; the base then ends the open
    /// transaction. An aborted operation of an explicit transaction records that cause first, so a
    /// later commit reports it.
    /// </remarks>
    protected override async ValueTask DisposeAsyncCore()
    {
        BlobOperation? operation;
        lock (_sync)
        {
            operation = _operation;
        }

        if (operation is not null)
        {
            await operation.AbortAsync(new DatabaseException("The blob session closed while the operation was running.")).ConfigureAwait(false);
        }
    }
}
