using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A graph session: runs GQL statements, the database's typed node and relationship operations
/// and <see cref="GraphSchema"/> operations one at a time, in autocommit or in its explicit
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's state, its explicit transaction and the "already active" check are the root
/// base's</b> (<see cref="DatabaseSession"/>, §6.4 of the concrete-types plan): BEGIN is refused
/// with "A transaction or operation is already active on this session." while the session's
/// transaction is usable, while another BEGIN runs, or while a statement holds the session; a
/// closed session refuses everything with "The session is closed."; and disposal ends the running
/// statement, then rolls the open transaction back as the session's teardown. This type supplies
/// the model's work: the isolation-level and offline refusals of BEGIN, the statements, and the
/// translation of the kernel's exceptions at the model boundary.
/// </para>
/// <para>
/// <b>A failed statement aborts the explicit transaction (#1188).</b> The transaction stays the
/// session's transaction until the caller commits, rolls back or disposes it; the session then
/// refuses every statement and BEGIN with <c>CG04007</c> until the caller rolls back. A failure
/// never turns later statements into autocommit writes. A statement holds the session (the base's
/// operation hold) from its start to its end, so a second statement on the session is refused
/// meanwhile, and so is a BEGIN.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root's language
/// base, <see cref="DatabaseLanguageSession{TRequest}"/> over <see cref="GraphRequest"/>, with an
/// internal constructor; <see cref="GraphDatabase.CreateSessionAsync"/> creates it. The session
/// inherits both execute members: GQL text, and the typed <see cref="GraphRequest"/>, whose two
/// leaves (<see cref="GraphQueryRequest"/> for rows, <see cref="GraphPathsQueryRequest"/> for
/// paths) are the only request types it takes. The database and the transaction are re-exposed
/// typed with <c>new</c> members over the base's public members.
/// </para>
/// </remarks>
public sealed class GraphDatabaseSession : DatabaseLanguageSession<GraphRequest>
{
    private static readonly string _errorSessionDisposeAsyncCoreDatabase = ErrorMessages.SessionDisposeAsyncCoreDatabase;

    private readonly GraphDatabase _database;
    private readonly object _sync = new();

    // The statement running on the session, which the session's teardown aborts; statements run
    // one at a time, so there is at most one.
    private GraphOperation? _operation;

    internal GraphDatabaseSession(GraphDatabase database)
        : base(database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the graph database this session is scoped to.
    /// </summary>
    public new GraphDatabase Database => _database;

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including an aborted
    /// transaction (<see cref="TransactionState.Faulted"/>) that still waits for the caller's
    /// rollback; null when none is open.
    /// </summary>
    public new GraphDatabaseTransaction? CurrentTransaction => (GraphDatabaseTransaction?)base.CurrentTransaction;

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
    /// transaction refuses work (<c>CG04007</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    public new async ValueTask<GraphDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => (GraphDatabaseTransaction)await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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
    /// transaction refuses work (<c>CG04007</c>); or the graph engine does not support
    /// <paramref name="isolationLevel"/>.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <remarks>
    /// The base refuses a closed session and an active transaction or operation before the
    /// isolation level, the disposed database and the offline database are checked.
    /// </remarks>
    public new async ValueTask<GraphDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => (GraphDatabaseTransaction)await base.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Parses and executes one statement. An aborted transaction permits only ROLLBACK (or failed
    /// COMMIT); other statements retain the transaction refusal, including malformed text. A parse or
    /// request-validation failure aborts the explicit transaction exactly as an
    /// execution failure does, so every failed statement has one outcome. The wire server runs its
    /// statements through it.
    /// </summary>
    /// <param name="parse">Parses and validates the statement text into its request.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The statement's result.</returns>
    internal async ValueTask<DatabaseResult> ExecuteStatementAsync(Func<GraphRequest> parse, CancellationToken cancellationToken)
    {
        ThrowIfNotOpen();
        _database.ThrowIfOffline();
        GraphRequest request;
        try
        {
            request = parse();
        }
        catch (DatabaseException error)
        {
            ThrowIfTransactionRefuses();
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
    /// statements (<c>CG04007</c>).
    /// </exception>
    internal GraphDatabaseTransaction? EnterOperation()
    {
        if (!TryEnterOperation())
        {
            throw new DatabaseException(ErrorMessages.DatabaseSessionEnterOperationDatabase);
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
    internal void Track(GraphOperation operation)
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
    internal void ReleaseOperation(GraphDatabaseTransaction? transaction)
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
    /// Aborts the explicit transaction for a statement that failed outside its operation: before it
    /// reached one (parsing, request validation) or after its operation completed (the wire server
    /// encoding or writing its result). A statement running concurrently on the session (a caller
    /// contract violation) owns the session; its own outcome decides the transaction's.
    /// </summary>
    /// <param name="cause">The failure the caller observes.</param>
    /// <returns>A task that completes once the transaction is aborted, or at once.</returns>
    internal async ValueTask AbortTransactionAsync(Exception cause)
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
            throw new DatabaseException(ErrorMessages.BeginTransactionCoreAsyncDatabase);
        }

        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        var transactionStart = (_database.Engine.EngineOptions.TimeProvider ?? TimeProvider.System).GetUtcNow();
        TransactionContext context;
        try
        {
            context = await _database.Coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A begin that met the offline storage (#1243) gets the coded refusal.
            var reported = _database.TranslateFailure(error);
            if (ReferenceEquals(reported, error))
            {
                throw;
            }

            throw reported;
        }

        return new GraphDatabaseTransaction(_database.Coordinator, context, _database, transactionStart);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The leaf chooses the result shape, never whether the request runs:
    /// <see cref="GraphPathsQueryRequest"/> returns paths and <see cref="GraphQueryRequest"/> rows.
    /// </remarks>
    protected override ValueTask<DatabaseResult> ExecuteCoreAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        _database.EnsureNotDisposed();
        _database.ThrowIfOffline();
        if (request.Statement.GqlExpression.TransactionCommand != GqlTransactionCommand.None &&
            !request.Statement.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return ExecuteTransactionControlAsync(request, cancellationToken);
        }
        return _database.RunAsync(this, operation =>
        {
            StartQueryBudget(operation, request.Limits, request.Statement.GqlExpression.Location);
            // Error-recovery trees still go through the normal diagnostic refusal path.
            if (request.Statement.GqlExpression.IsExplain &&
                !request.Statement.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                operation.EnsureActive();
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Paths)
                {
                    throw new GraphPlanningException("CG05001", ErrorMessages.SessionExecuteCoreAsyncGraphPlanning, request.Statement.GqlExpression.Location);
                }
                var planner = new GraphPlanner(_database, operation.Context.Snapshot);
                var plan = planner.Plan(request.Statement.GqlExpression, request.Parameters);
                return ValueTask.FromResult<DatabaseResult>(planner.Describe(plan, operation.QueryBudget));
            }
            return GraphPlanExecutor.ExecuteAsync(_database, operation, request.Statement,
                request.Parameters, cancellationToken, paths: request.Paths, scalarsOnly: request.ScalarsOnly, compositeValues: request.CompositeValues);
        }, cancellationToken);
    }

    internal GraphPlanExecutor.ExpansionBudget? LastStatementMetrics { get; private set; }

    private async ValueTask<DatabaseResult> ExecuteTransactionControlAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();
        var expression = request.Statement.GqlExpression;
        if (request.Paths)
        {
            var error = new GraphPlanningException("CG05001", ErrorMessages.TransactionPathsInvalid, expression.Location);
            await AbortTransactionAsync(error).ConfigureAwait(false);
            throw error;
        }
        var transaction = CurrentTransaction;
        if (expression.TransactionCommand == GqlTransactionCommand.Start)
        {
            ThrowIfTransactionRefuses();
            if (transaction is not null)
            {
                var error = new GraphPlanningException("CG04022", ErrorMessages.TransactionAlreadyOpen, expression.Location);
                await AbortTransactionAsync(error).ConfigureAwait(false);
                throw error;
            }
            await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (transaction is null)
            { throw new GraphPlanningException("CG04023", ErrorMessages.TransactionNotOpen, expression.Location); }
            if (expression.TransactionCommand == GqlTransactionCommand.Commit)
            { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }
            else { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); }
        }
        return new GraphMutationResult(0, []);
    }
    private readonly GraphQueryMemoryPool _queryMemoryPool = new();

    internal GraphPlanExecutor.ExpansionBudget StartQueryBudget(GraphOperation operation, Assimalign.Cohesion.Database.Graph.Protocol.GraphQueryLimits? limits,
        Assimalign.Cohesion.Database.Language.Location? location = null)
    {
        var options = _database.Engine.EngineOptions;
        long memory = limits?.MemoryBytes ?? options.QueryMemoryBudget;
        long work = limits?.WorkUnits ?? options.QueryWorkBudget;
        if (memory <= 0 || work <= 0 || memory > options.QueryMemoryBudget || work > options.QueryWorkBudget)
        { throw new GraphPlanningException("CG08021", ErrorMessages.QueryLimitsInvalid, location); }
        return operation.QueryBudget = LastStatementMetrics = new GraphPlanExecutor.ExpansionBudget(work, memory, _queryMemoryPool);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The graph session's text seam: the statement is parsed as GQL
    /// (<see cref="GraphQueryRequest.FromGql"/>) into the request the typed member takes, and a
    /// statement that fails to parse aborts an explicit transaction as an execution failure does.
    /// The wire server parses its statements itself and runs them through
    /// <see cref="ExecuteStatementAsync"/>.
    /// </remarks>
    protected override ValueTask<DatabaseResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
        => ExecuteStatementAsync(() => GraphQueryRequest.FromGql(statement, parameters), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Aborts the statement running on the session, if any, with "The graph session closed while
    /// the operation was running."; the base then ends the open transaction. An aborted statement
    /// of an explicit transaction records that cause first, so a later commit reports it.
    /// </remarks>
    protected override async ValueTask DisposeAsyncCore()
    {
        _queryMemoryPool.Dispose();
        GraphOperation? operation;
        lock (_sync)
        {
            operation = _operation;
        }

        if (operation is not null)
        {
            await operation.AbortAsync(new DatabaseException(_errorSessionDisposeAsyncCoreDatabase)).ConfigureAwait(false);
        }
    }
}
