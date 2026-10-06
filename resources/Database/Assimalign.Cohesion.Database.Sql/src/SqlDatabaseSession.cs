using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// A SQL database session, bound to the database's MVCC transaction manager: explicit and
/// auto-commit statements alike run under a <see cref="TransactionContext"/> paired with a storage
/// bracket, so visibility semantics never fork between the two paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's state, its explicit transaction and the "already active" check are the root
/// base's</b> (<see cref="DatabaseSession"/>, §6.4 of the concrete-types plan): a typed BEGIN is
/// refused with "A transaction or operation is already active on this session." while the
/// session's transaction is usable, a closed session refuses everything with "The session is
/// closed.", and disposal ends the open transaction as the session's teardown (a later commit of it
/// reports <c>COHSQLT005</c> naming the closure). This type supplies the model's work: the
/// isolation-level and offline refusals of BEGIN, the statements, the SQL transaction-control
/// statements (<c>BEGIN</c>, <c>COMMIT</c>, <c>ROLLBACK</c>) and the translation of the kernel's
/// exceptions at the model boundary.
/// </para>
/// <para>
/// <b>A statement is statement-atomic.</b> Inside an explicit transaction, a statement that fails
/// writes nothing and leaves the transaction active; the session never aborts its transaction for
/// a failed statement (the owner's 2026-10-04 per-statement decision). A statement is admitted
/// into the transaction through the base's operation admission, so a commit cannot start while it
/// runs, and a transaction that refuses work (its commit or rollback is running, or the kernel
/// ended it under its caller) refuses the statement. Statements do not hold the session (the
/// base's operation hold): sessions are single-threaded by contract, as before the bases.
/// </para>
/// <para>
/// <b>Transaction control through statement text.</b> <c>BEGIN</c> on a session with a usable
/// transaction and <c>COMMIT</c> or <c>ROLLBACK</c> without one are state misuse, reported as
/// diagnostics (<c>COHSQLT001</c>, <c>COHSQLT002</c>) without throwing or changing the transaction.
/// <c>BEGIN</c> on a session whose transaction refuses work throws that transaction's refusal, as
/// the typed BEGIN does; <c>COMMIT</c> and <c>ROLLBACK</c> end the transaction through the base, so
/// a <c>COMMIT</c> of a transaction the kernel ended rolls it back and throws <c>COHSQLT005</c>.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; <see cref="SqlDatabase.CreateSessionAsync"/> creates it. The database
/// and the transaction are re-exposed typed with <c>new</c> members over the base's public members.
/// </para>
/// </remarks>
public sealed class SqlDatabaseSession : DatabaseSession
{
    private readonly SqlDatabase _database;
    private readonly TransactionCoordinator _coordinator;
    private readonly SqlQueryExecutor _executor;
    private readonly SqlQueryParserOptions _parserOptions;
    private readonly string? _provisioningSchema;

    // The isolation level of an auto-commit statement and of a BEGIN statement. The SQL
    // isolation syntax (SET TRANSACTION ISOLATION LEVEL) that would change it is not implemented.
    private readonly IsolationLevel _isolationLevel = IsolationLevel.Snapshot;
    private SqlStatementMetrics? _lastStatementMetrics;

    /// <summary>Opens a session over a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="coordinator">The database's transaction coordinator.</param>
    /// <param name="executor">The statement executor.</param>
    /// <param name="parserOptions">
    /// The engine's parser options: statement text parses with its expression nesting limit, and
    /// a typed request nested deeper is refused (#1151).
    /// </param>
    /// <param name="provisioningSchema">The schema the provisioner's session owns, if any.</param>
    internal SqlDatabaseSession(
        SqlDatabase database,
        TransactionCoordinator coordinator,
        SqlQueryExecutor executor,
        SqlQueryParserOptions parserOptions,
        string? provisioningSchema = null)
        : base(database)
    {
        _database = database;
        _coordinator = coordinator;
        _executor = executor;
        _parserOptions = parserOptions;
        _provisioningSchema = provisioningSchema;
    }

    /// <summary>
    /// Gets the SQL database this session is scoped to.
    /// </summary>
    public new SqlDatabase Database => _database;

    /// <summary>
    /// Gets the session's explicit transaction until the caller ends it, including one the kernel
    /// ended under its caller (<see cref="TransactionState.Faulted"/>), which waits for the caller's
    /// rollback; null when none is open. A failed statement never ends it.
    /// </summary>
    public new SqlDatabaseTransaction? CurrentTransaction => (SqlDatabaseTransaction?)base.CurrentTransaction;

    /// <summary>
    /// Gets the previous statement's execution observability (access path,
    /// records examined) — the behavioral proof surface access-path tests read.
    /// </summary>
    internal SqlStatementMetrics? LastStatementMetrics => _lastStatementMetrics;

    /// <summary>
    /// Begins an explicit transaction at the default isolation level, <see cref="IsolationLevel.Snapshot"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="ObjectDisposedException">The session's database has been disposed.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; or the session's
    /// transaction refuses work (<c>COHSQLT005</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHSQLT004</c>, #1243).</exception>
    public new async ValueTask<SqlDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => (SqlDatabaseTransaction)await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Begins an explicit transaction at the requested isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction executes under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new transaction, now the session's transaction.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the transaction began.</exception>
    /// <exception cref="ObjectDisposedException">The session's database has been disposed.</exception>
    /// <exception cref="DatabaseException">
    /// The session is closed; a transaction or operation is already active on it; the session's
    /// transaction refuses work (<c>COHSQLT005</c>); or <paramref name="isolationLevel"/> is
    /// <see cref="IsolationLevel.Serializable"/>.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHSQLT004</c>, #1243).</exception>
    /// <remarks>
    /// The session begins an MVCC transaction context on the database's transaction manager
    /// alongside the physical storage bracket (paired under one sequence):
    /// <see cref="IsolationLevel.Snapshot"/> fixes the visibility snapshot (and the catalog capture
    /// system views read) at begin, <see cref="IsolationLevel.ReadCommitted"/> refreshes both per
    /// statement. <see cref="IsolationLevel.Serializable"/> is rejected: the engine has no
    /// serialization-conflict detection yet, and the root contract forbids running a transaction
    /// weaker than requested. The base refuses a closed session and an active transaction, and
    /// observes the token, before the isolation level and the offline database are checked.
    /// </remarks>
    public new async ValueTask<SqlDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => (SqlDatabaseTransaction)await base.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        _database.ThrowIfOffline();
        if (isolationLevel == IsolationLevel.Serializable)
        {
            throw new DatabaseException(
                "IsolationLevel.Serializable is not supported by the SQL engine yet: serialization-conflict " +
                "detection is a post-MVP feature, and the session contract forbids running weaker than requested. " +
                "Use IsolationLevel.Snapshot or IsolationLevel.ReadCommitted.");
        }

        TransactionContext context;
        try
        {
            context = await _coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateOffline(exception) is DatabaseOfflineException offline)
        {
            throw offline;
        }

        return new SqlDatabaseTransaction(_coordinator, context, _database,
            isolationLevel == IsolationLevel.Snapshot ? _executor.CaptureCatalogSnapshot() : null);
    }

    /// <inheritdoc />
    protected override ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken)
        => ExecuteRequestAsync(request, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text-execute seam: SQL sessions parse the statement with
    /// the SQL dialect, under the engine's expression nesting limit — this is what lets
    /// the wire-protocol server execute statement text without knowing any model
    /// language. A parse that runs out of stack fails with <c>COHSQLE004</c>, as any
    /// other walk over the statement does.
    /// </remarks>
    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
        => ExecuteRequestAsync(SqlQueryRequest.FromSql(statement, parameters, _parserOptions), cancellationToken);

    private async ValueTask<QueryResult> ExecuteRequestAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        _database.ThrowIfOffline();

        try
        {
            return await ExecuteStatementAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateOffline(exception, selfCommitting: IsSelfCommitting(request)) is var translated
            && !ReferenceEquals(translated, exception))
        {
            // A statement that met the offline storage (#1243) gets the coded refusal, unless its
            // work may survive the reopen: a self-committing DDL statement, or a bracket whose
            // commit record was written, is unconfirmed instead. The unconfirmed commit that took
            // the database offline keeps its own type.
            throw translated;
        }
        catch (InsufficientExecutionStackException exception)
        {
            // Every recursive walk over a statement (the system-relation scan, planning,
            // evaluation, CHECK validation) checks the stack before it descends (#1151). The
            // nesting limit bounds how deep a statement nests, not how much stack the walks need
            // on the thread that runs them, so a statement within a high configured limit, a
            // LIKE match backtracking through more wildcards than the stack holds, or a
            // statement run on a thread too small for it gets here, including a stored CHECK or
            // DEFAULT the statement reads back on first use. It is PostgreSQL's backstop
            // (SQLSTATE 54001): the statement fails as this statement's error, the auto-commit
            // context has rolled back, an explicit transaction stays active, and the session
            // stays usable.
            throw SqlEvaluationException.StatementTooComplex(exception);
        }
    }

    /// <summary>
    /// Reports whether a request is a self-committing statement: DDL, which runs only in
    /// auto-commit mode and commits durable brackets in the catalog and data file sets as it
    /// goes, before and independently of its transaction's commit record.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True for a <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> statement.</returns>
    private static bool IsSelfCommitting(QueryRequest request)
        => request is SqlQueryRequest { Statement.SqlExpression.CommandType:
            SqlQueryCommandType.Create or SqlQueryCommandType.Alter or SqlQueryCommandType.Drop };

    private async ValueTask<QueryResult> ExecuteStatementAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        // Typed requests may be constructed directly from a parser result rather
        // than FromSql. Never execute an error-recovery AST (notably ROLLBACK TO
        // must not become a full ROLLBACK while savepoints remain unsupported).
        if (request is SqlQueryRequest parsed)
        {
            foreach (var diagnostic in parsed.Statement.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    return new SqlQueryResult(QueryResultStatus.Error, affectedCount: 0,
                        [.. parsed.Statement.Diagnostics]);
                }
            }

            // A typed request was parsed by its caller, possibly with a higher nesting limit than
            // this engine's. The parser recorded how deep the statement nests, so the engine's
            // limit holds on this seam too, exactly as if the engine had parsed the text (#1151).
            // A statement over a subquery taken out of a parsed statement has no parser's measure
            // and is held to the depth of its own tree, which is what the walks below recurse over.
            if (parsed.Statement.ExpressionNestingDepth > _parserOptions.ExpressionNestingLimit)
            {
                return new SqlQueryResult(QueryResultStatus.Error, affectedCount: 0,
                    [NestingLimitDiagnostic(parsed.Statement.ExpressionNestingDepth)]);
            }

            SqlSystemViews.EnsureReadOnly(parsed.Statement.SqlExpression);
        }

        // Control commands bind to the session before an auto-commit context is
        // opened. Their result follows the ordinary query/wire diagnostic path.
        if (request is SqlQueryRequest { Statement.SqlExpression: SqlTransactionExpression control })
        {
            _lastStatementMetrics = null;
            return await ExecuteTransactionControlAsync(control, cancellationToken).ConfigureAwait(false);
        }

        // Inside an explicit transaction, the statement rides its context. A statement is
        // statement-atomic: its writes share one physical bracket that a failure rolls back, so a
        // failed statement writes nothing and the transaction stays active. Only a transaction that
        // is ending, or that the kernel ended under its caller, refuses statements, so a statement
        // never runs in a half-rolled-back transaction or silently autocommits (#1225).
        if (CurrentTransaction is { } transaction)
        {
            // The admission also keeps a commit from starting while the statement runs. A rollback
            // may still end the transaction underneath it (a host's rollback of a wire session's
            // transaction): the statement then fails, and the kernel applies nothing for it.
            if (!transaction.TryBeginStatement())
            {
                throw transaction.CreateStatementRefusal();
            }

            try
            {
                if (IsSelfCommitting(request))
                {
                    return TransactionDiagnostic("COHSQLT003",
                        "DDL requires auto-commit mode because the catalog does not enlist in session transactions.");
                }

                var scope = new SqlStatementContext(transaction.Context, _coordinator, _provisioningSchema,
                    _database.Name.ToString(), transaction.CatalogSnapshot ?? CaptureSystemViewSnapshot(request));
                _lastStatementMetrics = scope.Metrics;

                try
                {
                    return await _executor.ExecuteAsync(request, scope, cancellationToken).ConfigureAwait(false);
                }
                catch (TransactionDeadlockException exception)
                {
                    // The requester-closes-cycle victim: the statement failed and is
                    // retryable by construction — roll the transaction back and
                    // re-attempt. The session stays usable.
                    throw new DatabaseTransactionDeadlockException(exception.Message, exception);
                }
                catch (TransactionAbortedException exception)
                {
                    throw new DatabaseTransactionAbortedException(exception.Message, exception);
                }
            }
            finally
            {
                transaction.EndStatement();
            }
        }

        // Auto-commit semantics: a one-statement manager transaction, so
        // visibility and conflict semantics are identical to the explicit path.
        TransactionContext context;
        try
        {
            context = await _coordinator.BeginAsync(_isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateOffline(exception) is DatabaseOfflineException offline)
        {
            // Refused before the statement wrote anything, a self-committing one included.
            throw offline;
        }

        try
        {
            var scope = new SqlStatementContext(context, _coordinator, _provisioningSchema,
                _database.Name.ToString(), CaptureSystemViewSnapshot(request));
            _lastStatementMetrics = scope.Metrics;
            var result = await _executor.ExecuteAsync(request, scope, cancellationToken).ConfigureAwait(false);
            await _coordinator.CommitAsync(context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (TransactionCommitUnconfirmedException exception)
        {
            // The statement committed; only the durability of its commit record is unconfirmed.
            throw new DatabaseTransactionCommitUnconfirmedException(exception.Message, exception);
        }
        catch (TransactionDeadlockException exception)
        {
            await RollbackAutoCommitAsync(context).ConfigureAwait(false);
            throw new DatabaseTransactionDeadlockException(exception.Message, exception);
        }
        catch (TransactionAbortedException exception)
        {
            await RollbackAutoCommitAsync(context).ConfigureAwait(false);
            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
        catch
        {
            await RollbackAutoCommitAsync(context).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rolls a failed auto-commit statement's transaction back while it is still active. On an
    /// offline database it touches nothing (#1243): the undo could write nothing, its refusal
    /// would replace the statement's own failure, and the reopen's recovery aborts the
    /// transaction, which has no commit record.
    /// </summary>
    private async ValueTask RollbackAutoCommitAsync(TransactionContext context)
    {
        if (context.State == TransactionState.Active && !_database.IsOffline)
        {
            await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The <c>SQL0006</c> a typed request gets when its statement nests deeper than this engine's
    /// limit: the diagnostic the engine's own parse of the text would have reported, without its
    /// position, which only a parse finds.
    /// </summary>
    private Diagnostic NestingLimitDiagnostic(int depth) => new()
    {
        Code = "SQL0006",
        Message = $"Expression nesting of {depth} levels exceeds this engine's limit of {_parserOptions.ExpressionNestingLimit} levels.",
        Severity = DiagnosticSeverity.Error,
        Location = DiagnosticLocation.Absolute,
    };

    private async ValueTask<QueryResult> ExecuteTransactionControlAsync(
        SqlTransactionExpression control, CancellationToken cancellationToken)
    {
        var transaction = CurrentTransaction;
        if (control.CommandType == SqlQueryCommandType.Begin)
        {
            if (transaction is not null)
            {
                // A transaction that refuses work refuses BEGIN with its own refusal, as the typed
                // BEGIN does; a usable one makes BEGIN state misuse, reported, not thrown.
                ThrowIfTransactionRefuses();
                return TransactionDiagnostic("COHSQLT001", "BEGIN requires a session with no open transaction.");
            }

            await BeginTransactionAsync(_isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (transaction is null)
            {
                return TransactionDiagnostic("COHSQLT002", $"{control.CommandType.ToString().ToUpperInvariant()} requires an open transaction.");
            }

            // The base ends the transaction whatever the outcome; a COMMIT of a transaction the
            // kernel ended under its caller rolls it back and throws COHSQLT005.
            if (control.CommandType == SqlQueryCommandType.Commit)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private static QueryResult TransactionDiagnostic(string code, string message)
        => new SqlQueryResult(QueryResultStatus.Error, affectedCount: 0,
            [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }]);

    // Ordinary DML does not enumerate the catalog just to construct a context.
    // Explicit Snapshot transactions capture at BEGIN even if their first metadata
    // SELECT comes later; read committed and auto-commit capture at statement start.
    private SqlCatalogSnapshot? CaptureSystemViewSnapshot(QueryRequest request)
        => request is SqlQueryRequest sql && UsesSystemView(sql.Statement.SqlExpression)
            ? _executor.CaptureCatalogSnapshot() : null;

    /// <summary>Captures metadata once for nested SELECTs and INSERT sources as well as the outer relation.</summary>
    private static bool UsesSystemView(SqlQueryExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (query is SqlInsertExpression { SelectSource: not null } insert)
        {
            return UsesSystemView(insert.SelectSource);
        }
        if (query is not SqlSelectExpression select)
        {
            return false;
        }
        return select.From is not null && SqlSystemViews.Find(select.From) is not null
            || select.Columns.Any(column => UsesSystemViewExpression(column.Expression))
            || select.Joins.Any(join => UsesSystemViewExpression(join.Condition))
            || UsesSystemViewExpression(select.Where) || select.GroupBy.Any(UsesSystemViewExpression)
            || UsesSystemViewExpression(select.Having) || select.OrderBy.Any(order => UsesSystemViewExpression(order.Expression));
    }

    private static bool UsesSystemViewExpression(SqlExpression? expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression switch
        {
            null => false,
            SqlSubqueryExpression scalar => UsesSystemView(scalar.Select),
            SqlExistsExpression exists => UsesSystemView(exists.Subquery),
            SqlInExpression { Subquery: not null } member => UsesSystemView(member.Subquery) || UsesSystemViewExpression(member.Operand),
            _ => SqlPlanner.Children(expression).Any(UsesSystemViewExpression),
        };
    }
}
