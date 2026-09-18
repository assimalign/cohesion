using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal implementation of a SQL database session, bound to the database's
/// MVCC transaction manager: explicit and auto-commit statements alike run under
/// an <see cref="ITransactionContext"/> paired with a storage bracket, so
/// visibility semantics never fork between the two paths.
/// </summary>
internal sealed class SqlDatabaseSession : IDatabaseSession
{
    private readonly TransactionCoordinator _coordinator;
    private readonly SqlQueryExecutor _executor;
    private readonly string? _provisioningSchema;

    // B7 can push named scopes onto the same root transaction and attach undo
    // markers. B2 only ever pushes the root scope; nested BEGIN is an error.
    private readonly Stack<SqlTransactionScope> _transactionScopes = new();
    private IsolationLevel _isolationLevel = IsolationLevel.Snapshot;
    private SqlStatementMetrics? _lastStatementMetrics;
    private SessionState _state;

    internal SqlDatabaseSession(
        ISqlDatabase database,
        TransactionCoordinator coordinator,
        SqlQueryExecutor executor,
        string? provisioningSchema = null)
    {
        Database = database;
        _coordinator = coordinator;
        _executor = executor;
        _provisioningSchema = provisioningSchema;
        _state = SessionState.Open;
    }

    /// <inheritdoc />
    public IDatabase Database { get; }

    /// <inheritdoc />
    public SessionState State => _state;

    /// <inheritdoc />
    public IDatabaseTransaction? CurrentTransaction => ActiveScope?.Transaction;

    private SqlTransactionScope? ActiveScope
    {
        get
        {
            while (_transactionScopes.TryPeek(out var scope))
            {
                if (scope.Transaction.State == TransactionState.Active)
                {
                    return scope;
                }

                _transactionScopes.Pop();
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the previous statement's execution observability (access path,
    /// records examined) — the behavioral proof surface access-path tests read.
    /// </summary>
    internal SqlStatementMetrics? LastStatementMetrics => _lastStatementMetrics;

    /// <inheritdoc />
    public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(_isolationLevel, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The session begins an MVCC transaction context on the database's
    /// transaction manager alongside the physical storage bracket (paired under
    /// one sequence): <see cref="IsolationLevel.Snapshot"/> fixes the visibility
    /// snapshot at begin, <see cref="IsolationLevel.ReadCommitted"/> refreshes
    /// it per statement. <see cref="IsolationLevel.Serializable"/> is rejected —
    /// the engine has no serialization-conflict detection yet, and the root
    /// contract forbids running a transaction weaker than requested.
    /// </remarks>
    public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (isolationLevel == IsolationLevel.Serializable)
        {
            throw new DatabaseException(
                "IsolationLevel.Serializable is not supported by the SQL engine yet: serialization-conflict " +
                "detection is a post-MVP feature, and the session contract forbids running weaker than requested. " +
                "Use IsolationLevel.Snapshot or IsolationLevel.ReadCommitted.");
        }

        if (ActiveScope is not null)
        {
            throw new DatabaseException("A transaction is already active on this session.");
        }

        var context = await _coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        var transaction = new SqlDatabaseTransaction(_coordinator, context);
        _transactionScopes.Push(new SqlTransactionScope(transaction, isolationLevel,
            isolationLevel == IsolationLevel.Snapshot ? _executor.CaptureCatalogSnapshot() : null));
        return transaction;
    }

    /// <inheritdoc />
    public async ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

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

            SqlSystemViews.EnsureReadOnly(parsed.Statement.SqlExpression);
        }

        // Control commands bind to the session before an auto-commit context is
        // opened. Their result follows the ordinary query/wire diagnostic path.
        if (request is SqlQueryRequest { Statement.SqlExpression: SqlTransactionExpression control })
        {
            _lastStatementMetrics = null;
            return await ExecuteTransactionControlAsync(control, cancellationToken).ConfigureAwait(false);
        }

        // Inside an explicit transaction, the statement rides its context.
        if (ActiveScope is { } transactionScope)
        {
            if (request is SqlQueryRequest { Statement.SqlExpression.CommandType:
                SqlQueryCommandType.Create or SqlQueryCommandType.Alter or SqlQueryCommandType.Drop })
            {
                return TransactionDiagnostic("COHSQLT003",
                    "DDL requires auto-commit mode because the catalog does not enlist in session transactions.");
            }

            var scope = new SqlStatementContext(transactionScope.Transaction.Context, _coordinator, _provisioningSchema,
                Database.Name.ToString(), transactionScope.CatalogSnapshot ?? CaptureSystemViewSnapshot(request));
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

        // Auto-commit semantics: a one-statement manager transaction, so
        // visibility and conflict semantics are identical to the explicit path.
        var context = await _coordinator.BeginAsync(_isolationLevel, cancellationToken).ConfigureAwait(false);

        try
        {
            var scope = new SqlStatementContext(context, _coordinator, _provisioningSchema,
                Database.Name.ToString(), CaptureSystemViewSnapshot(request));
            _lastStatementMetrics = scope.Metrics;
            var result = await _executor.ExecuteAsync(request, scope, cancellationToken).ConfigureAwait(false);
            await _coordinator.CommitAsync(context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (TransactionDeadlockException exception)
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw new DatabaseTransactionDeadlockException(exception.Message, exception);
        }
        catch (TransactionAbortedException exception)
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw new DatabaseTransactionAbortedException(exception.Message, exception);
        }
        catch
        {
            if (context.State == TransactionState.Active)
            {
                await _coordinator.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The model-agnostic text-execute seam: SQL sessions parse the statement with
    /// the SQL dialect (<see cref="SqlQueryRequest.FromSql"/>) — this is what lets
    /// the wire-protocol server execute statement text without knowing any model
    /// language.
    /// </remarks>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return ExecuteAsync(SqlQueryRequest.FromSql(statement, parameters), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_state == SessionState.Closed)
        {
            return;
        }

        // Dispose the root once, even after B7 adds nested scopes sharing it.
        if (ActiveScope is { } scope)
        {
            await scope.Transaction.DisposeAsync().ConfigureAwait(false);
        }

        _transactionScopes.Clear();
        _state = SessionState.Closed;
    }

    private async ValueTask<QueryResult> ExecuteTransactionControlAsync(
        SqlTransactionExpression control, CancellationToken cancellationToken)
    {
        var scope = ActiveScope;
        if (control.CommandType == SqlQueryCommandType.Begin)
        {
            if (scope is not null)
            {
                return TransactionDiagnostic("COHSQLT001", "BEGIN requires a session with no open transaction.");
            }

            await BeginTransactionAsync(_isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (scope is null)
            {
                return TransactionDiagnostic("COHSQLT002", $"{control.CommandType.ToString().ToUpperInvariant()} requires an open transaction.");
            }

            if (control.CommandType == SqlQueryCommandType.Commit)
            {
                await scope.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await scope.Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            _transactionScopes.Clear();
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

    private static bool UsesSystemViewExpression(SqlExpression? expression) => expression switch
    {
        null => false,
        SqlSubqueryExpression scalar => UsesSystemView(scalar.Select),
        SqlExistsExpression exists => UsesSystemView(exists.Subquery),
        SqlInExpression { Subquery: not null } member => UsesSystemView(member.Subquery) || UsesSystemViewExpression(member.Operand),
        _ => SqlPlanner.Children(expression).Any(UsesSystemViewExpression),
    };

    private sealed record SqlTransactionScope(
        SqlDatabaseTransaction Transaction, IsolationLevel IsolationLevel, SqlCatalogSnapshot? CatalogSnapshot);

    private void ThrowIfNotOpen()
    {
        if (_state != SessionState.Open)
        {
            throw new DatabaseException($"Session is not open. Current state: {_state}.");
        }
    }
}
