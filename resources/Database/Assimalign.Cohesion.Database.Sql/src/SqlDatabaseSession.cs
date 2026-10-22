using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

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
/// writes nothing and leaves the transaction active (the owner's 2026-10-04 per-statement decision).
/// Wire result encoding is part of statement execution (#1651). A statement is admitted
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
/// A target table definition changed during a lock wait invalidates the bound statement plan.
/// Execution throws <see cref="DatabaseTransactionAbortedException"/> with diagnostic
/// <c>COHSQLT006</c>; retry in a fresh transaction. Adjacent FK changes rebuild before row reads.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root's language
/// base, <see cref="DatabaseLanguageSession{TRequest}"/> over <see cref="SqlQueryRequest"/>, with an
/// internal constructor; <see cref="SqlDatabase.CreateSessionAsync"/> creates it. The session
/// inherits both execute members: SQL text, and the typed <see cref="SqlQueryRequest"/>, which is
/// the only request type it takes. The database and the transaction are re-exposed typed with
/// <c>new</c> members over the base's public members.
/// </para>
/// </remarks>
public sealed class SqlDatabaseSession : DatabaseLanguageSession<SqlQueryRequest>
{
    private readonly SqlDatabase _database;
    private readonly TransactionCoordinator _coordinator;
    private readonly SqlQueryExecutor _executor;
    private readonly SqlQueryParserOptions _parserOptions;
    private readonly string? _provisioningSchema;
    private readonly TransactionSequence? _ddlLockOwner;

    // The isolation level of an auto-commit statement and of a BEGIN statement. The SQL
    // isolation syntax (SET TRANSACTION ISOLATION LEVEL) that would change it is not implemented.
    private readonly IsolationLevel _isolationLevel = IsolationLevel.Snapshot;
    private SqlStatementMetrics? _lastStatementMetrics;
    private TimeSpan? _statementTimeout;
    private CancellationTokenSource? _statementTimer;
    private CancellationToken _statementCallerToken;
    private TimeSpan? _lockTimeout;

    /// <summary>Opens a session over a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="coordinator">The database's transaction coordinator.</param>
    /// <param name="executor">The statement executor.</param>
    /// <param name="parserOptions">
    /// The engine's parser options: statement text parses with its expression nesting limit, and
    /// a typed request nested deeper is refused (#1151).
    /// </param>
    /// <param name="provisioningSchema">The schema the provisioner's session owns, if any.</param>
    /// <param name="ddlLockOwner">The apply's retained DDL lock owner, if any.</param>
    internal SqlDatabaseSession(
        SqlDatabase database,
        TransactionCoordinator coordinator,
        SqlQueryExecutor executor,
        SqlQueryParserOptions parserOptions,
        string? provisioningSchema = null,
        TransactionSequence? ddlLockOwner = null)
        : base(database)
    {
        _database = database;
        _coordinator = coordinator;
        _executor = executor;
        _parserOptions = parserOptions;
        _provisioningSchema = provisioningSchema;
        _ddlLockOwner = ddlLockOwner;
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
    /// Gets or sets this session's statement execution timeout override. Null inherits the engine
    /// option; zero disables it. Changes apply to subsequent statements. A started commit always
    /// completes. Like execution, setting this property requires single-threaded session use.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or exceeds int.MaxValue milliseconds.</exception>
    public TimeSpan? StatementTimeout
    {
        get => _statementTimeout;
        set
        {
            if (value is { } timeout) { SqlTimeoutSettings.Validate(timeout, nameof(value)); }
            _statementTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets this session's timeout override for each individual lock wait, including
    /// unique-index waits. Null inherits the engine option; zero disables it. Changes apply to
    /// subsequent statements and require single-threaded session use.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or exceeds int.MaxValue milliseconds.</exception>
    public TimeSpan? LockTimeout
    {
        get => _lockTimeout;
        set
        {
            if (value is { } timeout) { SqlTimeoutSettings.Validate(timeout, nameof(value)); }
            _lockTimeout = value;
        }
    }

    internal Action<long, long?>? ProvisioningProgress { get; set; }

    // Set only by the owning wire pump; embedded execution needs no wire buffers.
    internal SqlResultPreparation? ResultPreparation { get; set; }

    // Test-only observation of the real timer token, so commit-boundary proofs await its expiry.
    internal Action<CancellationToken>? StatementTimerArmed { get; set; }

    // Move the real timer's deadline to a deterministic apply/publication boundary in tests.
    internal void ExpireStatementTimerForTest() => _statementTimer!.CancelAfter(TimeSpan.FromMilliseconds(1));

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

        var transactionStart = (_database.Engine.EngineOptions.TimeProvider ?? TimeProvider.System).GetUtcNow();
        TransactionContext context;
        try
        {
            context = await _coordinator.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateFailure(exception) is var offline && !ReferenceEquals(offline, exception))
        {
            throw offline;
        }

        return new SqlDatabaseTransaction(_coordinator, context, _database,
            isolationLevel == IsolationLevel.Snapshot ? _executor.CaptureCatalogSnapshot() : null, transactionStart);
    }

    /// <inheritdoc />
    protected override ValueTask<DatabaseResult> ExecuteCoreAsync(SqlQueryRequest request, CancellationToken cancellationToken)
        => ExecuteRequestAsync(request, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The SQL session's text seam: the statement is parsed with the SQL dialect, under the
    /// engine's expression nesting limit, into the <see cref="SqlQueryRequest"/> the typed member
    /// takes. The SQL wire server executes the statement text it receives through this member of
    /// its typed session. A parse that runs out of stack fails with <c>COHSQLE004</c>, as any other
    /// walk over the statement does.
    /// </remarks>
    protected override ValueTask<DatabaseResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
        => ExecuteRequestAsync(SqlQueryRequest.FromSql(statement, parameters, _parserOptions, _database.Name), cancellationToken);

    /// <summary>Executes one request with a receipt that cannot attach to any later session request.</summary>
    internal ValueTask<DatabaseResult> ExecuteScriptAsync(SqlQueryRequest request, SqlCatalogScriptPublication receipt, CancellationToken cancellationToken)
        => ExecuteRequestAsync(request, cancellationToken, receipt);

    private ValueTask<DatabaseResult> ExecuteRequestAsync(SqlQueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt = null)
    {
        var statementTimeout = StatementTimeout ?? _database.Engine.EngineOptions.StatementTimeout;
        var lockTimeout = LockTimeout ?? _database.Engine.EngineOptions.LockTimeout;
        return request.Statement.SqlExpression is SqlTransactionExpression || (statementTimeout == TimeSpan.Zero && lockTimeout == TimeSpan.Zero)
            ? ExecuteUntimedRequestAsync(request, cancellationToken, scriptReceipt)
            : ExecuteTimedRequestAsync(request, statementTimeout, lockTimeout, cancellationToken, scriptReceipt);
    }

    private async ValueTask<DatabaseResult> ExecuteTimedRequestAsync(SqlQueryRequest request, TimeSpan statementTimeout,
        TimeSpan lockTimeout, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt)
    {
        using var timer = statementTimeout == TimeSpan.Zero ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer?.CancelAfter(statementTimeout);
        using var lockScope = lockTimeout == TimeSpan.Zero ? null : _coordinator.LockManager.EnterLockTimeout(lockTimeout);
        try
        {
            _statementTimer = timer;
            _statementCallerToken = cancellationToken;
            if (timer is not null) { StatementTimerArmed?.Invoke(timer.Token); }
            // Only execution receives the linked token. Auto-commit and self-committing DDL
            // retain #1246's uncancellable commit/cleanup boundary and deliver their result.
            return await ExecuteUntimedRequestAsync(request, timer?.Token ?? cancellationToken, scriptReceipt).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timer?.IsCancellationRequested == true)
        {
            throw SqlTimeoutException.Statement(exception, request.Statement.SqlExpression.Location);
        }
        catch (TransactionLockTimeoutException exception)
        {
            throw SqlTimeoutException.Lock(exception, request.Statement.SqlExpression.Location);
        }
        finally { _statementTimer = null; _statementCallerToken = default; }
    }

    private async ValueTask<DatabaseResult> ExecuteUntimedRequestAsync(SqlQueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt)
    {
        _database.ThrowIfOffline();
        // The statement's own metrics are the ones set after this point; a statement that fails
        // before it runs leaves the previous statement's, which never classify this one.
        var previousMetrics = _lastStatementMetrics;
        long readerEpoch = _database.TableReclamation.EnterStatement();
        try
        {
            if (_provisioningSchema is null && _database.SchemaProvisioner.Deferred is not null)
            {
                await _database.SchemaProvisioner.WaitForTablesAsync(request, cancellationToken).ConfigureAwait(false);
                _database.ThrowIfOffline();
            }
            using var ddl = _provisioningSchema is null && IsSelfCommitting(request)
                ? _database.DdlInterlock.EnterStatement(_database.Name.ToString()) : null;
            return await ExecuteStatementAsync(request, cancellationToken, scriptReceipt).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateFailure(exception, selfCommitted: SelfCommitted(request, previousMetrics)) is var translated
            && !ReferenceEquals(translated, exception))
        {
            // A statement that met the offline storage (#1243) gets the coded refusal, unless its
            // work may survive the reopen: a self-committing DDL statement that already committed a
            // catalog change a reopened database shows, or a bracket whose commit record was
            // written, is unconfirmed instead (#1272). The unconfirmed commit that took the database offline
            // keeps its own type. Both lead with COHSQLT004.
            throw translated;
        }
        catch (Exception exception) when (exception is DatabaseException or InsufficientExecutionStackException)
        {
            // Share the existing catch slot: an additional catch variable grows the Debug
            // async state machine and allocates another eight bytes on every statement.
            if (exception is DatabaseException && exception is not SqlEvaluationException { Code: SqlEvaluationException.StatementTooComplexCode })
            {
                ThrowLocatedFailure((DatabaseException)exception, request.Statement.SqlExpression);
                throw;
            }
            SqlDatabaseEventSource.Log.StatementTooComplex(_database.Name, -1);
            // Every recursive walk over a statement (the system-relation scan, planning,
            // evaluation, CHECK validation) checks the stack before it descends (#1151). The
            // nesting limit bounds how deep a statement nests, not how much stack the walks need
            // on the thread that runs them, so a statement within a high configured limit, or a
            // statement run on a thread too small for it gets here, including a stored CHECK or
            // DEFAULT the statement reads back on first use. It is PostgreSQL's backstop
            // (SQLSTATE 54001): the statement fails as this statement's error, the auto-commit
            // context has rolled back, an explicit transaction stays active, and the session
            // stays usable.
            if (exception is InsufficientExecutionStackException)
            {
                throw SqlEvaluationException.StatementTooComplex((InsufficientExecutionStackException)exception).At(request.Statement.SqlExpression.Location);
            }
            ThrowLocatedFailure((DatabaseException)exception, request.Statement.SqlExpression);
            throw;
        }
        finally
        {
            try
            {
                if (request.Statement.SqlExpression is SqlDropTableExpression or SqlDropSchemaExpression)
                {
                    _database.TableReclamation.Request();
                }
                TraceStatementCompletion(request, previousMetrics);
            }
            finally
            {
                // The session retains metrics after execution, but wire buffers belong to delivery.
                if (_lastStatementMetrics is not null) { _lastStatementMetrics.ResultPreparation = null; }
                _database.TableReclamation.ExitStatement(readerEpoch);
            }
        }
    }

    // Kept outside the async state machine: locating failures adds no successful-statement fields
    // or allocations. Preserve concrete exception contracts and any more specific expression span.
    private static void ThrowLocatedFailure(DatabaseException failure, SqlQueryExpression expression)
    {
        var original = failure;
        var location = expression.Location;
        if (expression.Text is { } text)
        {
            if (location is not null && (location.Start < 0 || location.End < location.Start || location.End > text.Length))
            { location = Location.Create(1, 1 + text.Count(character => character == '\n'), 0, text.Length); }
            if (failure.Diagnostics.Any(diagnostic =>
                diagnostic.Start is { } start && (start < 0 || start > text.Length || diagnostic.End is not { } end || end < start || end > text.Length)
                || diagnostic.Start is null && diagnostic.End is not null))
            {
                // Persisted boundaries discard their own locations. This final bound also refuses
                // malformed locations supplied by application code, without clipping a foreign span.
                failure = SqlExecutionException.FromPersisted(failure);
            }
        }
        var located = LocateFailure(failure, location);
        if (!ReferenceEquals(located, original)) { throw located; }
    }

    private static DatabaseException LocateFailure(DatabaseException failure, Location? location)
    {
        // The abort path retains this exact coded cause (#1364). Location attachment must not
        // substitute another exception for a storage failure already recorded on the transaction.
        if (failure.Diagnostics is [{ Code: "COHSQLT005" or "COHSQLE041" or "COHDBS003" }, ..]
            || SqlDatabase.IsStorageFailure(failure)) { return failure; }
        if (failure is SqlConstraintViolationException constraint) { return constraint.At(location); }
        if (failure is SqlExecutionException execution) { return execution.At(location); }
        if (failure is SqlEvaluationException evaluation) { return evaluation.At(location); }
        if (failure is SqlPlanningException planning) { return planning.At(location); }
        if (failure is SqlFunctionResultMismatchException mismatch) { return mismatch.At(location); }
        if (failure.Diagnostics.Count > 0 && (location is null || failure.Diagnostics.All(diagnostic => diagnostic.Start is not null))) { return failure; }
        // Child failures already translated by the preceding catch keep their authored type.
        if (failure is not (SqlCatalogException or DatabaseObjectLockedException) && failure.GetType() != typeof(DatabaseException)) { return failure; }
        var diagnostics = failure.Diagnostics.Count == 0
            ? new[] { new Diagnostic { Code = failure is DatabaseObjectLockedException ? SqlExecutionException.InsufficientPrivilegeCode : SqlExecutionException.InternalErrorCode,
                Message = failure.Message, Severity = DiagnosticSeverity.Error } }
            : failure.Diagnostics;
        var located = diagnostics.Select(diagnostic => diagnostic.Start is not null || location is null ? diagnostic : new Diagnostic
        {
            Code = diagnostic.Code, Message = diagnostic.Message, Severity = diagnostic.Severity,
            Start = location.Start, End = location.End, Line = location.StartLine, Location = DiagnosticLocation.Absolute,
        }).ToArray();
        if (failure is DatabaseObjectLockedException ownership)
        { return new DatabaseObjectLockedException(ownership.ObjectName, ownership.OwningSchema, ownership.Operation, ownership.Message, located[0]); }
        if (failure is SqlCatalogException)
        { return new SqlCatalogException(located[0].Code!, failure.Message, failure, located[0]); }
        return new DatabaseException(failure.Message, located, failure);
    }

    private void TraceStatementCompletion(SqlQueryRequest request, SqlStatementMetrics? previousMetrics)
    {
        // Metrics are already owned by execution. Keep reporting locals out of the async
        // state machine: Debug hoists them, increasing the allocation even while tracing is off.
        if (_lastStatementMetrics is { } metrics && !ReferenceEquals(metrics, previousMetrics))
        {
            if (metrics.IsPlanned)
            {
                SqlDatabaseEventSource.Log.StatementPlanned(_database.Name, metrics);
            }
            if (metrics.SelfCommits > 0)
            {
                SqlDatabaseEventSource.Log.DdlSelfCommitted(_database.Name, request.Statement.SqlExpression.CommandType);
            }
        }
    }

    /// <summary>
    /// Reports whether a request is a self-committing statement: DDL, which runs only in
    /// auto-commit mode and commits durable brackets in the catalog and data file sets as it
    /// goes, before and independently of its transaction's commit record.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True for a <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> statement.</returns>
    private static bool IsSelfCommitting(SqlQueryRequest request)
        => request.Statement.SqlExpression.CommandType is
            SqlQueryCommandType.Create or SqlQueryCommandType.Alter or SqlQueryCommandType.Drop;

    /// <summary>
    /// Reports whether a failed request is a self-committing statement that had committed at
    /// least one catalog change a reopened database shows when it failed: the executor records
    /// each catalog self-commit that publishes, alters or removes a definition on the statement's
    /// metrics once its commit returned. Such a statement's failure on an offline storage is
    /// unconfirmed, because what it committed survives the reopen; a DDL statement that committed
    /// no such change is refused, like any other statement (#1272), even after durable steps
    /// nothing can reach (CREATE TABLE's identity reservation, index trees no catalog entry
    /// describes yet). Before #1272 every DDL statement that met an offline storage was reported as
    /// unconfirmed, even one a concurrent failure refused before it wrote anything.
    /// </summary>
    /// <param name="request">The failed request.</param>
    /// <param name="previousMetrics">The session's last metrics before the request ran.</param>
    /// <returns>True when the statement committed a visible catalog change before it failed.</returns>
    private bool SelfCommitted(SqlQueryRequest request, SqlStatementMetrics? previousMetrics)
        => IsSelfCommitting(request)
            && _lastStatementMetrics is { SelfCommits: > 0 } metrics
            && !ReferenceEquals(metrics, previousMetrics);

    private ValueTask<DatabaseResult> ExecuteStatementAsync(SqlQueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt)
        => IsSelfCommitting(request) ? ExecuteMetadataStatementAsync(request, cancellationToken, scriptReceipt) : ExecuteStatementCoreAsync(request, cancellationToken, scriptReceipt);

    private async ValueTask<DatabaseResult> ExecuteMetadataStatementAsync(SqlQueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt)
    {
        // DROP SCHEMA takes its definition lease in the executor, after all table locks.
        if (request.Statement.SqlExpression is SqlDropSchemaExpression)
        {
            if (_ddlLockOwner is null) { _database.DdlInterlock.CheckDefinitionAdmission(_database.Name.ToString()); }
            return await ExecuteStatementCoreAsync(request, cancellationToken, scriptReceipt).ConfigureAwait(false);
        }
        bool definition = request.Statement.SqlExpression
            is SqlCreateFunctionExpression or SqlDropFunctionExpression or SqlCreateDomainExpression or SqlDropDomainExpression
                or SqlCreateSchemaExpression or SqlCreateViewExpression or SqlDropViewExpression;
        using var metadata = _ddlLockOwner is not null
            ? await _database.DdlInterlock.EnterMetadataAsync(definition, cancellationToken).ConfigureAwait(false)
            : _database.DdlInterlock.EnterMetadata(_database.Name.ToString(), definition);
        return await ExecuteStatementCoreAsync(request, cancellationToken, scriptReceipt).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseResult> ExecuteStatementCoreAsync(SqlQueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt)
    {
        // Typed requests may be constructed directly from a parser result rather
        // than FromSql. Never execute an error-recovery AST (notably ROLLBACK TO
        // must not become a full ROLLBACK while savepoints remain unsupported).
        foreach (var diagnostic in request.Statement.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                if (diagnostic.Code is "SQL0006" or "SQL0007")
                {
                    SqlDatabaseEventSource.Log.StatementTooComplex(_database.Name, request.Statement.ExpressionNestingDepth);
                }
                if (diagnostic.Code == "SQL0007")
                {
                    throw SqlEvaluationException.StatementTooComplex(new InsufficientExecutionStackException(
                        $"SQL parse error {diagnostic.Code}: {diagnostic.Message}"));
                }
                throw new DatabaseParseException($"SQL parse error {diagnostic.Code}: {diagnostic.Message}", request.Statement.Diagnostics);
            }
        }

        // A typed request was parsed by its caller, possibly with a higher nesting limit than
        // this engine's. The parser recorded how deep the statement nests, so the engine's
        // limit holds on this seam too, exactly as if the engine had parsed the text (#1151).
        // A statement over a subquery taken out of a parsed statement has no parser's measure
        // and is held to the depth of its own tree, which is what the walks below recurse over.
        if (request.Statement.ExpressionNestingDepth > _parserOptions.ExpressionNestingLimit)
        {
            SqlDatabaseEventSource.Log.StatementTooComplex(_database.Name, request.Statement.ExpressionNestingDepth);
            var diagnostic = NestingLimitDiagnostic(request.Statement.ExpressionNestingDepth);
            throw new DatabaseParseException($"SQL parse error {diagnostic.Code}: {diagnostic.Message}", [diagnostic]);
        }

        SqlSystemViews.EnsureReadOnly(request.Statement.SqlExpression);

        // Control commands bind to the session before an auto-commit context is
        // opened. Their result follows the ordinary query/wire diagnostic path.
        if (request.Statement.SqlExpression is SqlTransactionExpression control)
        {
            // Control requests bypass the executor, but their maps obey the same contract.
            _ = SqlQueryExecutor.NormalizeParameters(request.Parameters);
            _lastStatementMetrics = null;
            // Control commands have a fixed, zero-count completion, including COMMIT.
            if (ResultPreparation is { } preparation)
            {
                await preparation.PrepareAsync(new SqlQueryResult(DatabaseResultStatus.Success, 0), cancellationToken).ConfigureAwait(false);
            }
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

                TransactionContext? snapshotPin = null;
                var lockMark = _coordinator.LockManager.MarkStatement(transaction.Context.Sequence);

                try
                {
                    snapshotPin = await BeginSnapshotPinAsync(transaction.Context, cancellationToken).ConfigureAwait(false);

                    // The scope captures the statement's snapshot after the pin began, so the pin's
                    // floor is at or below the statement's (#1363). It runs under the transaction's own
                    // context, not a pinned view: anything phase two reads through that context's
                    // snapshot must see writers that committed while the statement waited for a lock
                    // (a cascade's index deletes did, until #1370 matched them by stamps).
                    var scope = new SqlStatementContext(transaction.Context, _coordinator, _provisioningSchema,
                        _database.Name.ToString(), transaction.CatalogSnapshot, transactionStart: transaction.TransactionStart, resultPreparation: ResultPreparation);
                    _lastStatementMetrics = scope.Metrics;
                    return await ExecuteDataOrExplainAsync(request, scope, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TransactionDeadlockException || SqlDatabase.IsStorageFailure(exception))
                {
                    if (SqlDatabase.IsStorageFailure(exception))
                    {
                        throw transaction.RecordStorageFailure(_database.TranslateFailure(exception));
                    }
                    // The requester-closes-cycle victim: the statement failed and is
                    // retryable by construction — roll the transaction back and
                    // re-attempt. The session stays usable.
                    throw new DatabaseTransactionDeadlockException(exception.Message, exception);
                }
                catch (Exception exception) when (exception is TransactionLockTimeoutException
                    || (exception is OperationCanceledException && _statementTimer?.IsCancellationRequested == true
                        && !_statementCallerToken.IsCancellationRequested))
                {
                    // The executor has rolled back any failed physical bracket before this catch.
                    // Ordinary caller cancellation retains its prior locking contract.
                    // Preserve grants from earlier statements, and never release an offline or
                    // abandoned writer's locks while recovery/deferred undo still owns its data.
                    if (!_database.IsOffline) { _coordinator.LockManager.ReleaseSince(lockMark); }
                    throw;
                }
                catch (TransactionAbortedException exception)
                {
                    throw new DatabaseTransactionAbortedException(exception.Message, exception);
                }
                finally
                {
                    if (transaction.HasStorageFailure)
                    {
                        await ReleaseFailedStatementAsync(snapshotPin, transaction).ConfigureAwait(false);
                    }
                    else { await ReleaseSnapshotPinAsync(snapshotPin).ConfigureAwait(false); }
                }
            }
            finally
            {
                transaction.EndStatement();
            }
        }

        // Auto-commit semantics: a one-statement manager transaction, so
        // visibility and conflict semantics are identical to the explicit path.
        var transactionStart = (_database.Engine.EngineOptions.TimeProvider ?? TimeProvider.System).GetUtcNow();
        TransactionContext context;
        try
        {
            context = await _coordinator.BeginAsync(_isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_database.TranslateFailure(exception) is var offline && !ReferenceEquals(offline, exception))
        {
            // Refused before the statement wrote anything, a self-committing one included.
            throw offline;
        }

        try
        {
            var scope = new SqlStatementContext(context, _coordinator, _provisioningSchema,
                _database.Name.ToString(), null, _ddlLockOwner, ProvisioningProgress, scriptReceipt, transactionStart, _database.DdlInterlock, ResultPreparation);
            _lastStatementMetrics = scope.Metrics;
            var result = await ExecuteDataOrExplainAsync(request, scope, cancellationToken).ConfigureAwait(false);
            if (scriptReceipt is { } receipt && !IsSelfCommitting(request) && result.Status == DatabaseResultStatus.Success)
            {
                await CompleteScriptStatementAsync(result, context, receipt, cancellationToken).ConfigureAwait(false);
            }
            // Execution is complete: disarm the statement deadline before entering commit.
            // The linked caller token is also absent from the commit, as #1246 requires.
            _statementTimer?.CancelAfter(Timeout.InfiniteTimeSpan);
            // Catalog DDL has already committed and finished its mandatory cleanup.
            // Deliver that result even if the caller canceled after the durable commit.
            // Execution completed: auto-commit owns the end from here, as in every model.
            await _coordinator.CommitAsync(context).ConfigureAwait(false);
            return result;
        }
        catch (TransactionCommitUnconfirmedException exception)
        {
            // The statement committed; only the durability of its commit record is unconfirmed.
            throw _database.CreateUnconfirmedCommit(exception);
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

    private Task<DatabaseResult> ExecuteDataOrExplainAsync(SqlQueryRequest request, SqlStatementContext scope, CancellationToken cancellationToken)
    {
        if (request.Statement.SqlExpression is not SqlExplainExpression explain)
        {
            if (ResultPreparation is not null && IsSelfCommitting(request))
            {
                return ExecuteSelfCommittingResultAsync(request, scope, cancellationToken);
            }
            var execution = _executor.ExecuteAsync(request, scope, cancellationToken);
            // Reads encode during delivery. Embedded execution keeps its direct task path.
            return ResultPreparation is not null && request.Statement.SqlExpression.CommandType is
                SqlQueryCommandType.Insert or SqlQueryCommandType.Update or SqlQueryCommandType.Delete
                    ? PrepareExecutionResultAsync(execution, scope, cancellationToken) : execution;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SqlSystemViews.EnsureReadOnly(explain.Statement);
        var parameters = SqlQueryExecutor.NormalizeParameters(request.Parameters);
        _database.Definitions.EnsureDefinitions(cancellationToken);
        var planner = new SqlPlanner(_database.Catalog, parameters, _database.Definitions.Functions, cancellationToken, scope);
        try
        {
            var plan = planner.Plan(explain.Statement);
            scope.Metrics.IsPlanned = true;
            var result = SqlPlanner.Describe(plan, request.Statement.Diagnostics);
            return Task.FromResult<DatabaseResult>(result);
        }
        catch (SqlUnsupportedQueryException exception)
        {
            return Task.FromResult<DatabaseResult>(new SqlQueryResult(DatabaseResultStatus.Error, affectedCount: 0, [.. exception.Diagnostics]));
        }
    }

    private static async Task<DatabaseResult> PrepareExecutionResultAsync(Task<DatabaseResult> execution,
        SqlStatementContext scope, CancellationToken cancellationToken)
        => await scope.PrepareResultAsync(await execution.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<DatabaseResult> ExecuteSelfCommittingResultAsync(SqlQueryRequest request,
        SqlStatementContext scope, CancellationToken cancellationToken)
    {
        // Every supported DDL success returns only a zero-count completion. Prepare that fixed
        // result before invoking the executor: catalog publication may commit inside execution.
        var prepared = await scope.PrepareResultAsync(new SqlQueryResult(DatabaseResultStatus.Success, 0), cancellationToken).ConfigureAwait(false);
        var result = await _executor.ExecuteAsync(request, scope, cancellationToken).ConfigureAwait(false);
        return result.Status == DatabaseResultStatus.Success ? prepared : result;
    }

    private async ValueTask CompleteScriptStatementAsync(DatabaseResult result, TransactionContext transaction, SqlCatalogScriptPublication receipt, CancellationToken cancellationToken)
    {
        if (result is DatabaseResultSet rows)
        {
            await foreach (var row in rows.GetRowsAsync(cancellationToken).ConfigureAwait(false)) { _ = row; }
        }
        await SqlScriptDataReceipt.AppendAsync(_database, transaction, receipt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Begins the snapshot pin of a statement in a <see cref="IsolationLevel.ReadCommitted"/>
    /// transaction: a snapshot transaction of its own, begun before the statement captures its
    /// snapshot, as the Documents, Graph and Blob operations begin theirs.
    /// </summary>
    /// <param name="transaction">The explicit transaction's context.</param>
    /// <param name="cancellationToken">Observed by the begin.</param>
    /// <returns>The pin, or null when the transaction's snapshot is fixed at its begin.</returns>
    /// <remarks>
    /// <para>
    /// The version purge reclaims below the transaction manager's prune bound, to which a
    /// read-committed transaction adds only its own sequence: its snapshot is captured afresh on
    /// every access. The statement's snapshot keeps the floor of the moment it was captured, which
    /// can be lower, because a writer that began before this transaction was still in flight then.
    /// Once that writer commits, nothing but this pin keeps the purge from reclaiming the versions
    /// it tombstoned while the statement still reads them, which would drop those rows from the
    /// statement's result. The pin's snapshot is captured first, so its floor is at or below the
    /// statement's, and at or below that of any snapshot the transaction's context captures later
    /// in the statement (#1363).
    /// </para>
    /// <para>
    /// The pin only holds the bound. The statement still runs under the transaction's own context,
    /// never a <see cref="TransactionContext.PinStatementSnapshot"/> view. Under the first cut's
    /// view, a cascade that waited for a child row's writer matched the child's index entries
    /// through the statement's older snapshot, found nothing, and left the deleted child's unique
    /// entry live. Since #1370 the index delete matches the reference's live entry by its stamps,
    /// not through any snapshot.
    /// </para>
    /// </remarks>
    private async ValueTask<TransactionContext?> BeginSnapshotPinAsync(TransactionContext transaction, CancellationToken cancellationToken)
        => transaction.IsolationLevel == IsolationLevel.ReadCommitted
            ? await _coordinator.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false)
            : null;

    // Error-only async work stays outside the statement's normal state machine. The finally
    // already has a non-generic ValueTask awaiter, so this adds no field per successful statement.
    private async ValueTask ReleaseFailedStatementAsync(TransactionContext? snapshotPin, SqlDatabaseTransaction transaction)
    {
        Exception? cleanupFailure = null;
        try { await ReleaseSnapshotPinAsync(snapshotPin).ConfigureAwait(false); }
        catch (Exception failure) { cleanupFailure = failure; }
        await transaction.AbortStorageFailureAsync(cleanupFailure).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a statement's snapshot pin, on every path out of the statement. It always ends rolled
    /// back, with no token: it wrote nothing. On an offline database it touches nothing (#1243),
    /// and the reopen's recovery aborts it, as it does the auto-commit context below.
    /// </summary>
    /// <param name="snapshotPin">The pin, or null when the statement has none.</param>
    private async ValueTask ReleaseSnapshotPinAsync(TransactionContext? snapshotPin)
    {
        if (snapshotPin?.State == TransactionState.Active && !_database.IsOffline)
        {
            await _coordinator.RollbackAsync(snapshotPin, CancellationToken.None).ConfigureAwait(false);
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

    private async ValueTask<DatabaseResult> ExecuteTransactionControlAsync(
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

        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private static DatabaseResult TransactionDiagnostic(string code, string message)
        => new SqlQueryResult(DatabaseResultStatus.Error, affectedCount: 0,
            [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }]);
}
