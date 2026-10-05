using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every session: a lightweight, scoped execution context within a database, the entry
/// point for statement execution and explicit transactions.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session owns its explicit transaction.</b> <see cref="BeginTransactionAsync(IsolationLevel, CancellationToken)"/>
/// registers the transaction the leaf begins, and <see cref="CurrentTransaction"/> returns it until
/// the caller ends it, including a transaction an operation aborted
/// (<see cref="TransactionState.Faulted"/>), which waits for the caller's rollback.
/// </para>
/// <para>
/// <b>One "already active" check, with one message</b> (§6.4 of the concrete-types plan): BEGIN is
/// refused with "A transaction or operation is already active on this session." while the
/// session's transaction is usable, while another BEGIN runs, or while the leaf holds the session
/// for an operation (<see cref="TryEnterOperation"/>); while the session's transaction refuses work
/// (an operation aborted it), BEGIN gets that transaction's refusal instead, as every command but
/// the block's end does in PostgreSQL's failed transaction block
/// (<c>src/backend/tcop/postgres.c:1151-1163</c>). Before the bases, five sessions carried the check
/// with three messages.
/// </para>
/// <para>
/// <b>Disposal</b> closes the session, lets the leaf end its running operations
/// (<see cref="DisposeAsyncCore"/>), and then ends the open transaction as the session's teardown:
/// it is rolled back, and a caller that still holds it gets the model's coded error from a later
/// commit. Both steps run whatever the first threw, and the failures are reported together in one
/// <see cref="AggregateException"/>. A session never commits implicitly. Sessions are
/// single-threaded by contract; the base's checks only keep a contract violation from corrupting
/// the session's state.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> Every public member is non-virtual and calls
/// a protected core after its checks. The database is fixed by the protected constructor; a leaf
/// re-exposes it typed with a <c>new</c> property over a typed field of its own, and its typed
/// transaction with a <c>new</c> member that awaits <see cref="BeginTransactionAsync(IsolationLevel, CancellationToken)"/>,
/// never <see cref="BeginTransactionCoreAsync"/>. The leaves live in the model assemblies, so the
/// constructor is <c>protected</c>. Until phase 6 the base also implements
/// <see cref="IDatabaseSession"/>.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseSession : IDatabaseSession
{
    /// <summary>
    /// The one message of the "already active" check.
    /// </summary>
    internal const string AlreadyActiveMessage = "A transaction or operation is already active on this session.";

    private readonly DatabaseInstance _database;
    private readonly object _sync = new();
    private DatabaseTransaction? _transaction;
    private bool _beginning;
    private bool _operating;
    private bool _closed;

    /// <summary>
    /// Initializes a new session scoped to a database.
    /// </summary>
    /// <param name="database">The database the session is scoped to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    protected DatabaseSession(DatabaseInstance database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Gets the database this session is scoped to.
    /// </summary>
    public DatabaseInstance Database => _database;

    /// <summary>
    /// Gets the lifecycle state of the session.
    /// </summary>
    public SessionState State
    {
        get
        {
            lock (_sync)
            {
                return _closed ? SessionState.Closed : SessionState.Open;
            }
        }
    }

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including a transaction an
    /// operation aborted (<see cref="TransactionState.Faulted"/>) that waits for the caller's
    /// rollback; null when none is open, and once the session's teardown ended it.
    /// </summary>
    public DatabaseTransaction? CurrentTransaction
    {
        get
        {
            lock (_sync)
            {
                return OpenTransactionLocked;
            }
        }
    }

    private DatabaseTransaction? OpenTransactionLocked => _transaction is { IsOpen: true } transaction ? transaction : null;

    /// <summary>
    /// Begins an explicit transaction at the default isolation level, <see cref="IsolationLevel.Snapshot"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="DatabaseException">The session is closed, or a transaction or operation is already active on it.</exception>
    public ValueTask<DatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);

    /// <summary>
    /// Begins an explicit transaction at the requested isolation level. An engine may run it at a
    /// stronger level than requested, never weaker.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction executes under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; the session's
    /// transaction refuses work (its refusal); or the engine does not support
    /// <paramref name="isolationLevel"/>.
    /// </exception>
    public async ValueTask<DatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfClosedLocked();
            if (OpenTransactionLocked is { } open)
            {
                throw open.IsUsable ? new DatabaseException(AlreadyActiveMessage) : open.CreateRefusal();
            }

            if (_beginning || _operating)
            {
                throw new DatabaseException(AlreadyActiveMessage);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _beginning = true;
        }

        try
        {
            var transaction = await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session's transaction core returned null.");
            bool closed;
            lock (_sync)
            {
                closed = _closed;
                if (!closed)
                {
                    _transaction = transaction;
                }
            }

            if (closed)
            {
                // The session closed while the transaction began: the transaction never becomes
                // the session's, and nothing it could hold survives the closed session.
                await transaction.DisposeAsync().ConfigureAwait(false);
                throw CreateClosedException();
            }

            return transaction;
        }
        finally
        {
            lock (_sync)
            {
                _beginning = false;
            }
        }
    }

    /// <summary>
    /// Executes a typed request against the database.
    /// </summary>
    /// <param name="request">The request to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">The session is closed, or the statement failed.</exception>
    public ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Executes statement text in the session's own language, binding the given named parameter
    /// values: the model-agnostic text seam the wire servers use.
    /// </summary>
    /// <param name="statement">The statement text to parse and execute.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    /// <exception cref="ArgumentException"><paramref name="statement"/> is null, empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseParseException">The statement text failed to parse in the session's language.</exception>
    /// <exception cref="DatabaseException">The session is closed, or the statement failed.</exception>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteCoreAsync(statement, parameters, cancellationToken);
    }

    /// <summary>
    /// Closes the session: the leaf ends its running operations (<see cref="DisposeAsyncCore"/>),
    /// then the open transaction is rolled back as the session's teardown. Idempotent.
    /// </summary>
    /// <returns>A task that completes once the session is closed.</returns>
    /// <exception cref="AggregateException">One or both steps failed; both ran, and the failures are its inner exceptions.</exception>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        List<Exception>? failures = null;
        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            (failures ??= []).Add(failure);
        }

        DatabaseTransaction? transaction;
        lock (_sync)
        {
            transaction = OpenTransactionLocked;
            _transaction = null;
        }

        if (transaction is not null)
        {
            try
            {
                // The transaction object stays with its caller: a later rollback is a no-op, and a
                // later commit fails with the model's coded error naming the closure.
                await transaction.CloseAsync(new DatabaseException("The session closed before the transaction ended.")).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        // Always an aggregate, as the engine's disposal and the Graph, Documents and Blob sessions
        // report theirs: a caller catches one type whatever failed.
        if (failures is not null)
        {
            throw new AggregateException("The session failed to close.", failures);
        }
    }

    /// <summary>
    /// Holds the session for one operation of the leaf (a statement, an operation, a stream) that
    /// excludes BEGIN and every other operation, until <see cref="ExitOperation"/>. A model whose
    /// session runs one operation at a time uses it; while it is held, BEGIN is refused with the
    /// one "already active" message.
    /// </summary>
    /// <returns>True when the session was free and is now held; false while a BEGIN or another operation holds it.</returns>
    /// <exception cref="DatabaseException">The session is closed.</exception>
    protected bool TryEnterOperation()
    {
        lock (_sync)
        {
            ThrowIfClosedLocked();
            if (_beginning || _operating)
            {
                return false;
            }

            _operating = true;
            return true;
        }
    }

    /// <summary>
    /// Releases the hold <see cref="TryEnterOperation"/> took.
    /// </summary>
    protected void ExitOperation()
    {
        lock (_sync)
        {
            _operating = false;
        }
    }

    /// <summary>
    /// Throws the session's refusal when its open transaction refuses work: an operation aborted it,
    /// the kernel ended it under its caller, or the caller's commit or rollback is running. A leaf
    /// calls it before it parses or starts an operation.
    /// </summary>
    /// <exception cref="DatabaseException">The open transaction refuses work.</exception>
    protected void ThrowIfTransactionRefuses()
    {
        DatabaseTransaction? transaction;
        lock (_sync)
        {
            transaction = OpenTransactionLocked;
        }

        if (transaction is { IsUsable: false })
        {
            throw transaction.CreateRefusal();
        }
    }

    /// <summary>
    /// Throws <see cref="DatabaseException"/> once the session is closed.
    /// </summary>
    /// <exception cref="DatabaseException">The session is closed.</exception>
    protected void ThrowIfClosed()
    {
        lock (_sync)
        {
            ThrowIfClosedLocked();
        }
    }

    /// <summary>
    /// Begins the leaf's kernel transaction and creates its transaction. Called once the base
    /// reserved the session for the BEGIN: no transaction or operation is active.
    /// </summary>
    /// <param name="isolationLevel">The requested isolation level; the leaf refuses one its engine does not support.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The new transaction.</returns>
    protected abstract ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a typed request on an open session.
    /// </summary>
    /// <param name="request">The request; never null.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The query result.</returns>
    protected abstract ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Parses and executes statement text on an open session.
    /// </summary>
    /// <param name="statement">The statement text; never null, empty or white space.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The query result.</returns>
    protected abstract ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the leaf's running operations once the session is closed, before the base ends the
    /// session's transaction. Called once. The default does nothing.
    /// </summary>
    /// <returns>A task that completes once the leaf's operations ended.</returns>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    private void ThrowIfClosedLocked()
    {
        if (_closed)
        {
            throw CreateClosedException();
        }
    }

    private static DatabaseException CreateClosedException() => new("The session is closed.");

    IDatabase IDatabaseSession.Database => _database;

    IDatabaseTransaction? IDatabaseSession.CurrentTransaction => CurrentTransaction;

    async ValueTask<IDatabaseTransaction> IDatabaseSession.BeginTransactionAsync(CancellationToken cancellationToken)
        => await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    async ValueTask<IDatabaseTransaction> IDatabaseSession.BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        => await BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
}
