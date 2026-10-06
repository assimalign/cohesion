using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A document session: runs OQL statements and its own collection and document operations one at
/// a time, in autocommit or in its explicit transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's state, its explicit transaction and the "already active" check are the root
/// base's</b> (<see cref="DatabaseSession"/>, §6.4 of the concrete-types plan): BEGIN is refused
/// with "A transaction or operation is already active on this session." while the session's
/// transaction is usable, while another BEGIN runs, or while a statement holds the session; a
/// closed session refuses BEGIN and both execute seams with "The session is closed.", while its
/// collection operations and a collection's document operations run in it check the database
/// first (disposed, then offline) and then report the closed session with the same message; and
/// disposal ends the running statement, then rolls the open transaction back as the session's
/// teardown. This type supplies the model's work: the isolation-level and offline refusals of
/// BEGIN, the statements, and the translation of the kernel's exceptions at the model boundary.
/// </para>
/// <para>
/// <b>A failed statement aborts the explicit transaction (#1225, the contract #1188 set for
/// Graph).</b> The transaction stays the session's transaction until the caller commits, rolls back
/// or disposes it; the session then refuses every statement and BEGIN with <c>COHDBD001</c> until
/// the caller rolls back. A failure never turns later statements into autocommit writes. A
/// statement (an OQL statement, a collection operation of the session, or a document operation of
/// a <see cref="DocumentCollection"/> run in the session) holds the session (the base's operation
/// hold) from its start to its end, so a second statement on the session is refused meanwhile, and
/// so is a BEGIN.
/// </para>
/// <para>
/// <b>Option B (concrete-types plan, §6.6).</b> The session exposes its collection operations
/// itself (<see cref="CreateCollectionAsync"/>, <see cref="GetCollectionAsync"/>,
/// <see cref="DropCollectionAsync"/>, <see cref="GetCollectionsAsync"/>), and
/// <see cref="Database"/> is the unbound <see cref="DocumentDatabase"/>, whose own collection
/// operations run in autocommit outside any session. Disposing the session closes the session;
/// disposing its database closes the database for every session, and the engine refuses to reopen
/// it (<see cref="ObjectDisposedException"/>) until it is dropped or the engine is recreated.
/// Before phase 4 the session's database was a session-bound view whose disposal closed the
/// session, so <c>Dispose</c> meant two things on one type.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; <see cref="DocumentDatabase.CreateSessionAsync"/> creates it. The
/// database and the transaction are re-exposed typed with <c>new</c> members over the base's
/// public members.
/// </para>
/// </remarks>
public sealed class DocumentDatabaseSession : DatabaseSession
{
    private readonly DocumentDatabase _database;
    private readonly object _sync = new();

    // The statement running on the session, which the session's teardown aborts; statements run
    // one at a time, so there is at most one.
    private DocumentOperation? _operation;

    internal DocumentDatabaseSession(DocumentDatabase database)
        : base(database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the document database this session is scoped to: the unbound database, whose disposal
    /// closes the database, not the session (option B, §6.6 of the concrete-types plan).
    /// </summary>
    /// <remarks>
    /// The database's own collection operations run in autocommit, never in this session's
    /// transaction. A write through it (<see cref="DocumentDatabase.CreateCollectionAsync(string, CancellationToken)"/>,
    /// <see cref="DocumentDatabase.DropCollectionAsync(string, CancellationToken)"/>) while the
    /// session's explicit transaction has written waits for that transaction's writer lock (the
    /// engine has one writer at a time) until the transaction ends or the call's token is
    /// canceled; inside a transaction, use the session's own collection operations
    /// (<see cref="CreateCollectionAsync"/>, <see cref="DropCollectionAsync"/>).
    /// </remarks>
    public new DocumentDatabase Database => _database;

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including an aborted
    /// transaction (<see cref="TransactionState.Faulted"/>) that still waits for the caller's
    /// rollback; null when none is open.
    /// </summary>
    public new DocumentDatabaseTransaction? CurrentTransaction => (DocumentDatabaseTransaction?)base.CurrentTransaction;

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
    /// transaction refuses work (<c>COHDBD001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    public new async ValueTask<DocumentDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => (DocumentDatabaseTransaction)await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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
    /// transaction refuses work (<c>COHDBD001</c>); or the document engine does not support
    /// <paramref name="isolationLevel"/>.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <remarks>
    /// The base refuses a closed session and an active transaction or operation before the
    /// isolation level, the disposed database and the offline database are checked.
    /// </remarks>
    public new async ValueTask<DocumentDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => (DocumentDatabaseTransaction)await base.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new document collection as a statement of this session: in its explicit
    /// transaction when one is open, otherwise in autocommit.
    /// </summary>
    /// <param name="name">The name of the collection to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created collection, bound to this session.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// <paramref name="name"/> names a system collection, or a collection with the same name already
    /// exists; the session is closed, another statement holds it, or its transaction refuses
    /// statements (<c>COHDBD001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The document catalog changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<DocumentCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.CreateCollectionAsync(name, this, cancellationToken);

    /// <summary>
    /// Opens an existing document collection as a statement of this session.
    /// </summary>
    /// <param name="name">The name of the collection to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened collection, bound to this session.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The collection does not exist in the statement's snapshot; the session is closed, another
    /// statement holds it, or its transaction refuses statements (<c>COHDBD001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<DocumentCollection> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.GetCollectionAsync(name, this, cancellationToken);

    /// <summary>
    /// Drops a document collection, its documents and its indexes as a statement of this session.
    /// </summary>
    /// <param name="name">The name of the collection to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the collection is dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseObjectLockedException">The collection is owned by a schema.</exception>
    /// <exception cref="DatabaseException">
    /// <paramref name="name"/> names a system collection, or the collection does not exist; the
    /// session is closed, another statement holds it, or its transaction refuses statements
    /// (<c>COHDBD001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The collection, its documents or its indexes changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _database.DropCollectionAsync(name, this, cancellationToken);

    /// <summary>
    /// Enumerates the collections of the database as one statement of this session, run when the
    /// enumeration starts; the collections are then yielded.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of the collections, bound to this session, in ordinal name order.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed (also while the collections are yielded).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed (also while the collections are yielded), another statement holds it,
    /// or its transaction refuses statements (<c>COHDBD001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBD002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public IAsyncEnumerable<DocumentCollection> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => _database.GetCollectionsAsync(this, cancellationToken);

    /// <summary>
    /// Parses and executes one statement. An aborted transaction refuses the statement before it is
    /// parsed; a parse failure aborts the explicit transaction exactly as an execution failure does,
    /// so every failed statement has one outcome, as in PostgreSQL and Neo4j.
    /// </summary>
    /// <param name="parse">Parses the statement text into its request.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The statement's result.</returns>
    private async ValueTask<QueryResult> ExecuteStatementAsync(Func<QueryRequest> parse, CancellationToken cancellationToken)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
        ThrowIfTransactionRefuses();
        QueryRequest request;
        try
        {
            request = parse();
        }
        catch (DatabaseException error)
        {
            await AbortTransactionAsync(error).ConfigureAwait(false);
            throw;
        }

        return await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the session for one statement and admits it into the explicit transaction, if one is
    /// open: the statement's start. The statement releases both through
    /// <see cref="ReleaseOperation"/>.
    /// </summary>
    /// <returns>The explicit transaction the statement runs in, or null for autocommit.</returns>
    /// <exception cref="DatabaseException">
    /// The session is closed; another statement or a BEGIN holds it; or its transaction refuses
    /// statements (<c>COHDBD001</c>).
    /// </exception>
    internal DocumentDatabaseTransaction? EnterOperation()
    {
        if (!TryEnterOperation())
        {
            throw new DatabaseException("Dispose the active document operation before starting another operation on this session.");
        }

        var transaction = CurrentTransaction;
        if (transaction is null || transaction.TryBeginStatement())
        {
            return transaction;
        }

        ExitOperation();
        throw transaction.CreateStatementRefusal();
    }

    /// <summary>
    /// Records the statement that holds the session, so the session's teardown aborts it.
    /// </summary>
    /// <param name="operation">The statement's operation.</param>
    /// <exception cref="DatabaseException">The session closed while the statement started.</exception>
    internal void Track(DocumentOperation operation)
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
    /// Ends a statement <see cref="EnterOperation"/> started: its admission into the transaction, if
    /// any, and its hold on the session.
    /// </summary>
    /// <param name="transaction">The explicit transaction the statement ran in, or null.</param>
    internal void ReleaseOperation(DocumentDatabaseTransaction? transaction)
    {
        transaction?.EndStatement();
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
    /// Aborts the explicit transaction for a statement that failed before it reached an operation
    /// (parsing). A statement running concurrently on the session (a caller contract violation)
    /// owns the session; its own outcome decides the transaction's.
    /// </summary>
    /// <param name="cause">The failure the caller observes.</param>
    /// <returns>A task that completes once the transaction is aborted, or at once.</returns>
    private async ValueTask AbortTransactionAsync(Exception cause)
    {
        if (CurrentTransaction is not { AcceptsStatements: true } transaction)
        {
            return;
        }

        bool held;
        try
        {
            held = TryEnterOperation();
        }
        catch (DatabaseException) when (State == SessionState.Closed)
        {
            // The session closed meanwhile: its teardown ends the transaction.
            return;
        }

        if (!held)
        {
            return;
        }

        try
        {
            await transaction.AbortForFailedStatementAsync(cause).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted))
        {
            throw new DatabaseException("The document engine supports Snapshot and ReadCommitted isolation.");
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

        return new DocumentDatabaseTransaction(_database.Coordinator, context, _database);
    }

    /// <inheritdoc />
    protected override ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        if (request.Statement is not OqlQueryStatement statement)
        {
            throw new DatabaseException("A Documents session accepts only OQL statements.");
        }

        return _database.RunAsync(this, operation => DocumentPlanExecutor.ExecuteAsync(_database, operation, statement,
            request.Parameters, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text seam Studio uses: the statement is parsed as OQL
    /// (<see cref="DocumentQueryRequest.FromOql"/>), and a statement that fails to parse aborts an
    /// explicit transaction as an execution failure does.
    /// </remarks>
    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
        => ExecuteStatementAsync(() => DocumentQueryRequest.FromOql(statement, parameters), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Aborts the statement running on the session, if any, with "The document session closed
    /// while the operation was running."; the base then ends the open transaction. An aborted
    /// statement of an explicit transaction records that cause first, so a later commit reports it.
    /// </remarks>
    protected override async ValueTask DisposeAsyncCore()
    {
        DocumentOperation? operation;
        lock (_sync)
        {
            operation = _operation;
        }

        if (operation is not null)
        {
            await operation.AbortAsync(new DatabaseException("The document session closed while the operation was running.")).ConfigureAwait(false);
        }
    }
}
