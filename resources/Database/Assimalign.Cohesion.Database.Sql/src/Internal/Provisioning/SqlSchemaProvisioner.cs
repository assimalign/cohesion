using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Diffs, renders, applies, verifies and records the compiled schema owned by one SQL database:
/// the imperative <see cref="SqlDatabase.ApplySchemaAsync"/> path and phase 6 of the engine
/// builder's build (<see cref="SqlDeclaredDatabase"/>) share it.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlSchemaProvisioner")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSchemaProvisioner.DebugView))]
internal sealed partial class SqlSchemaProvisioner
{
    private static readonly string _errorSqlSchemaProvisionerApplyAsyncFailure4 = ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure4;

    private static CompiledSchemaReferentialAction CompiledDeleteAction(SqlCatalogReferentialAction action) => action switch
    {
        SqlCatalogReferentialAction.Restrict => CompiledSchemaReferentialAction.Restrict,
        SqlCatalogReferentialAction.Cascade => CompiledSchemaReferentialAction.Cascade,
        _ => throw new SqlSchemaMigrationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.CompiledDeleteActionInvalid, action)),
    };

    private static CompiledSchemaReferentialAction CompiledUpdateAction(SqlCatalogReferentialAction action) => action switch
    {
        SqlCatalogReferentialAction.Restrict => CompiledSchemaReferentialAction.Restrict,
        SqlCatalogReferentialAction.Cascade => CompiledSchemaReferentialAction.Cascade,
        SqlCatalogReferentialAction.NoAction => CompiledSchemaReferentialAction.NoAction,
        SqlCatalogReferentialAction.SetNull => CompiledSchemaReferentialAction.SetNull,
        SqlCatalogReferentialAction.SetDefault => CompiledSchemaReferentialAction.SetDefault,
        _ => throw new SqlSchemaMigrationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.CompiledUpdateActionInvalid, action)),
    };

    private readonly SqlDatabase _database;
    private readonly SqlCatalog _catalog;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private string? _validatedHash;
    private bool _validatedQualifiers;
    private string? _formatCheckedStateDocument;
    private string? _formatCheckedPendingDocument;

    // Instance-local test hooks, reached only by this assembly's tests. A skip must not
    // enter any catalog traversal; crash tests capture durable storage after each step.
    internal int CatalogWalkCount { get; private set; }
    internal SqlSchemaMigrationPlan? LastPlan { get; private set; }
    internal Action<SqlMigrationScriptStep>? StepApplied { get; set; }

    internal SqlSchemaProvisioner(SqlDatabase database, SqlCatalog catalog)
    {
        _database = database;
        _catalog = catalog;
        // Existing databases passed the document gate before their data file set opened.
        _formatCheckedStateDocument = catalog.SchemaState?.CanonicalDocument;
        _formatCheckedPendingDocument = catalog.PendingMigration?.CanonicalDocument;
    }

    internal static void ValidateRecordedFormats(SqlCatalog catalog)
    {
        ValidateRecordedFormat(catalog.SchemaState?.CanonicalDocument);
        ValidateRecordedFormat(catalog.PendingMigration?.CanonicalDocument);
    }

    private void ValidateRecordedFormats()
    {
        string? state = _catalog.SchemaState?.CanonicalDocument;
        string? pending = _catalog.PendingMigration?.CanonicalDocument;
        if (!ReferenceEquals(state, _formatCheckedStateDocument)) { ValidateRecordedFormat(state); }
        if (!ReferenceEquals(pending, _formatCheckedPendingDocument)) { ValidateRecordedFormat(pending); }
        _formatCheckedStateDocument = state;
        _formatCheckedPendingDocument = pending;
    }

    private static void ValidateRecordedFormat(string? document)
    {
        if (document is null) { return; }
        try
        {
            using JsonDocument json = JsonDocument.Parse(document);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("format", out JsonElement format) &&
                format.ValueKind == JsonValueKind.String && format.GetString() == SqlCompiledSchema.CurrentFormat)
            {
                return;
            }
        }
        catch (JsonException)
        {
            // A damaged marker still follows the existing live-catalog reconstruction path.
            // A valid document of any other format must reach the schema package's gate.
            return;
        }
        _ = SqlCompiledSchemaSerializer.Deserialize(document);
    }

    internal async ValueTask<SqlSchemaMigrationResult> ApplyAsync(
        SqlCompiledSchema schema,
        CancellationToken cancellationToken,
        bool deferDataScalingSteps = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRecordedFormats();
        _database.Definitions.EnsureDefinitions(cancellationToken);
        if (deferDataScalingSteps && TryGetDeferredResult(schema) is { } deferredResult)
        {
            return deferredResult;
        }
        // The worker retains this gate after handoff. Never queue outside the lock
        // manager: a caller may own a transaction the current migration needs.
        if (!await _applyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new DatabaseObjectLockedException(schema.Name, schema.Name, "APPLY SCHEMA");
        }
        BeginTrace(schema, "Apply");
        TransactionContext? locks = null;
        IDisposable? ddl = null;
        bool transferred = false;
        try
        {
            ddl = _database.DdlInterlock.EnterApply(schema.Name);
            TraceStage("Validate", SqlProvisioningCodes.PolicyRefused);
            ValidateSupportedSchema(schema);
            TraceStage("Planning", SqlProvisioningCodes.PolicyRefused);
            if (_catalog.PendingMigration is { } intent && !string.Equals(intent.ContentHash, schema.Hash, StringComparison.Ordinal))
            {
                throw new SqlSchemaMigrationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure, SqlProvisioningCodes.PolicyRefused, Describe()));
            }
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            if (IsApplied(recorded, schema))
            {
                TraceOutcome("Skipped", 0);
                return new SqlSchemaMigrationResult(recorded!.ContentHash, schema.Hash, 0, WasAlreadyApplied: true);
            }

            ValidateCatalogOwnership(schema);
            SqlCompiledSchema? current = ReadCurrentSchema(recorded, schema);
            SqlSchemaMigrationPlan plan;
            try
            {
                plan = SqlSchemaMigrationPlanner.Plan(current, schema);
            }
            catch (SqlSchemaMigrationException refused)
            {
                // The planner knows the schemas, not where they are applied.
                throw new SqlSchemaMigrationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure2, SqlProvisioningCodes.PolicyRefused, Describe(), refused.Message),
                    refused);
            }

            LastPlan = plan;
            SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(plan, current);
            TraceStage("Prepare", SqlProvisioningCodes.StepFailed, stepCount: script.Steps.Count);
            var applied = new List<SqlMigrationScriptStep>(script.Steps.Count);
            var deferred = deferDataScalingSteps ? FindDeferredSteps(script) : [];

            if (deferred.Count > 0)
            {
                // Even the first cheap DDL commit must be recoverable by database name
                // alone. Keep the complete target durable until convergence clears it.
                await _catalog.SavePendingMigrationAsync(
                    CreateDeferredIntent(schema, deferred, script.Steps.Count), cancellationToken).ConfigureAwait(false);
            }

            // This read-only context owns table locks across self-committing statements.
            // Its rollback only releases locks: no schema/data changes enlist in it.
            locks = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await using SqlDatabaseSession session = _database.CreateSchemaSession(schema.Name, cancellationToken, locks.Sequence);
            try
            {
                foreach (SqlMigrationScriptStep step in script.Steps)
                {
                    if (deferred.Contains(step))
                    {
                        continue;
                    }
                    TraceStep(script, step);
                    await session.ExecuteAsync(step.Request, cancellationToken).ConfigureAwait(false);
                    applied.Add(step);
                    StepApplied?.Invoke(step);
                }

                if (deferred.Count > 0)
                {
                    // Release the completed inline tables, then reserve every pending table
                    // before handing the engine to a caller. The worker owns this transaction.
                    TraceStage("Handoff", SqlProvisioningCodes.StepFailed);
                    await _database.Coordinator.RollbackAsync(locks, CancellationToken.None).ConfigureAwait(false);
                    locks = null;
                    await PrepareDeferredAsync(schema, deferred, script.Steps.Count, cancellationToken).ConfigureAwait(false);
                    transferred = true;
                    TraceOutcome("Deferred");
                    return new SqlSchemaMigrationResult(plan.SourceHash, schema.Hash, applied.Count, WasAlreadyApplied: false);
                }

                // IF NOT EXISTS and concurrent ad-hoc DDL must not turn an unowned or
                // mismatched object into a trusted marker. Capture before validation;
                // SaveSchemaState checks the generation again under the catalog lock.
                TraceStage("Finalize", SqlProvisioningCodes.StepFailed);
                ulong generation = _catalog.DdlGeneration;
                if (FirstCatalogDifference(schema) is { } difference)
                {
                    throw new SqlSchemaMigrationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure3, Describe(), difference));
                }

                await _catalog.SaveSchemaStateAsync(
                    new SqlCatalogSchemaState(schema.Hash, schema.CanonicalDocument, generation),
                    cancellationToken).ConfigureAwait(false);
                _formatCheckedStateDocument = schema.CanonicalDocument;
            }
            catch (Exception failure)
            {
                TraceStepFailure(schema, script, failure);
                CompensationResult compensation;
                if (locks is null && applied.Count > 0 && !_database.IsOffline)
                {
                    // The deferred handoff released the inline reservation. Compensation
                    // must acquire locks with a live transaction identity,
                    // even when cancellation interrupted that handoff.
                    locks = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None).ConfigureAwait(false);
                    await using var compensationSession = _database.CreateSchemaSession(schema.Name, CancellationToken.None, locks.Sequence);
                    compensation = await CompensateAsync(compensationSession, applied).ConfigureAwait(false);
                }
                else
                {
                    compensation = await CompensateAsync(session, applied).ConfigureAwait(false);
                }

                // A canceled token can also surface as the failure of the step it interrupted (a
                // commit whose record could not be written is an aborted transaction), so the
                // token decides, not only the failure's type: a compensated, canceled apply is a
                // cancellation, as BuildAsync and this method document.
                if (compensation.IsComplete &&
                    (failure is OperationCanceledException || cancellationToken.IsCancellationRequested))
                {
                    if (failure is OperationCanceledException)
                    {
                        throw;
                    }

                    throw new OperationCanceledException(
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorSqlSchemaProvisionerApplyAsyncFailure4, Describe(), schema.Name),
                        failure,
                        cancellationToken);
                }

                string state = compensation.IsComplete
                    ? ErrorMessages.CompensationComplete
                    : ErrorMessages.CompensationIncomplete;
                Exception inner = compensation.Failures.Count == 0
                    ? failure
                    : new AggregateException(new[] { failure }.Concat(compensation.Failures));
                string where = applied.Count < script.Steps.Count
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MigrationStepFailureContext, applied.Count + 1, script.Steps.Count, Describe(script.Steps[applied.Count].Operation))
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaRecordFailureContext, script.Steps.Count);
                // Keep the named dependent view visible at the schema-apply boundary,
                // rather than requiring callers to inspect the compensation's inner error.
                string dependency = failure is DatabaseException databaseFailure &&
                    databaseFailure.Diagnostics.Any(diagnostic => diagnostic.Code == "CS03302")
                    ? " " + failure.Message : string.Empty;
                throw new SqlSchemaMigrationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure5, SqlProvisioningCodes.StepFailed, Describe(), schema.Name, where, state, dependency),
                    inner);
            }

            TraceOutcome("Applied");
            return new SqlSchemaMigrationResult(
                plan.SourceHash,
                schema.Hash,
                plan.Operations.Count,
                WasAlreadyApplied: false);
        }
        catch (Exception failure) when (CaptureTraceFailure(schema, failure))
        {
            // The filter only records; it always declines the exception.
            throw;
        }
        finally
        {
            try
            {
                if (locks?.State == TransactionState.Active && !_database.IsOffline)
                {
                    await _database.Coordinator.RollbackAsync(locks, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                ddl?.Dispose();
                EndTrace();
                if (transferred)
                {
                    _database.Engine.ScheduleProvisioning(Deferred!);
                }
                else
                {
                    _applyGate.Release();
                }
            }
        }
    }

    /// <summary>
    /// Verifies, without running any DDL, that the database holds exactly the schema: the recorded
    /// hash and generation match the declaration and the current catalog generation.
    /// </summary>
    /// <param name="schema">The declared schema.</param>
    /// <param name="cancellationToken">Observed before the nonblocking admission check.</param>
    /// <returns>The result, always already applied.</returns>
    /// <exception cref="DatabaseObjectLockedException">An apply, verification or deferred migration is already active.</exception>
    /// <exception cref="SqlSchemaMigrationException">The database drifted (<c>CS03103</c>).</exception>
    internal async ValueTask<SqlSchemaMigrationResult> VerifyAsync(
        SqlCompiledSchema schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _database.Definitions.EnsureDefinitions(cancellationToken);
        if (!await _applyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new DatabaseObjectLockedException(schema.Name, schema.Name, "VERIFY SCHEMA");
        }
        BeginTrace(schema, "Verify");
        try
        {
            TraceStage("Validate", SqlProvisioningCodes.PolicyRefused);
            ValidateRecordedFormats();
            ValidateSupportedSchema(schema);
            TraceStage("Verify", SqlProvisioningCodes.Drift, stepCount: 0);
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            if (IsApplied(recorded, schema))
            {
                TraceOutcome("Verified");
                return new SqlSchemaMigrationResult(recorded!.ContentHash, schema.Hash, 0, WasAlreadyApplied: true);
            }

            string reason = recorded is null
                ? ErrorMessages.SchemaProvisionerVerifyAsyncFailure2
                : !string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal)
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaProvisionerVerifyAsyncFailure3, recorded.ContentHash)
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaProvisionerVerifyAsyncFailure4, recorded.Generation, _catalog.DdlGeneration);

            // Name the first object that differs, so the developer need not diff the catalog by hand.
            reason = AppendCatalogDifference(reason, schema);

            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaProvisionerVerifyAsyncFailure, SqlProvisioningCodes.Drift, Describe(), schema.Hash, reason));
        }
        catch (Exception failure) when (CaptureTraceFailure(schema, failure))
        {
            throw;
        }
        finally
        {
            EndTrace();
            _applyGate.Release();
        }
    }

    // Keep verification's formatting local out of the Debug async state machine.
    private string AppendCatalogDifference(string reason, SqlCompiledSchema schema)
        => FirstCatalogDifference(schema) is { } difference
            ? reason + string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclarationDifferenceContext, difference)
            : reason;

    /// <summary>
    /// Describes what a compiled schema declares that the SQL DDL executor cannot provision yet,
    /// or returns null when it declares nothing of the kind: the engine builder refuses such a
    /// declaration before any file is touched (phase 3 of its build), and an apply refuses it before
    /// any step runs.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The first unsupported object, described for a message, or null.</returns>
    internal static string? DescribeUnsupported(SqlCompiledSchema schema)
        => schema.Principals.Count > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PrincipalDdlUnavailable, schema.Principals[0].Name)
            : null;

    /// <summary>
    /// The domain a schema's <c>schema.Type&lt;T&gt;</c> declares (phase E3): a domain over the
    /// declared storage type, its precision and scale, without a check.
    /// </summary>
    /// <param name="type">The compiled type.</param>
    /// <returns>The domain.</returns>
    /// <exception cref="ArgumentException">The type's name cannot name a domain, or its storage type is not a SQL type.</exception>
    internal static SqlType CreateDomain(CompiledSchemaType type)
        => SqlType.CreateDomain(type.Name, SqlType.ForStorage(new DatabaseTypeInfo(type.StorageType, null, type.Precision, type.Scale)));

    /// <summary>
    /// Resolves the type a compiled column is declared with: its own storage type, or, for a column
    /// declared with a SQL type (<c>table.Column(selector, sqlType)</c>, or a member of a
    /// <c>schema.Type&lt;T&gt;</c> type), what that name resolves to in the engine's type catalog, as
    /// the DDL that creates the column will resolve it.
    /// </summary>
    /// <param name="column">The compiled column.</param>
    /// <param name="types">The engine's type catalog.</param>
    /// <param name="domain">The domain's name when the column is of one; otherwise null.</param>
    /// <returns>The storage type and its constraints.</returns>
    /// <exception cref="DatabaseException">The declared type does not resolve, or its storage type is not the column's.</exception>
    internal static DatabaseTypeInfo ResolveColumnType(CompiledSchemaColumn column, SqlTypeCatalog types, out string? domain)
    {
        domain = null;
        if (column.CustomType is not { } declared)
        {
            return new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision,
                SqlTypeModifiers.NormalizeScale(column.Type, column.Precision, column.Scale));
        }

        if (types.Resolve(declared, out string? error) is not { } type)
        {
            throw error is not null
                ? new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerResolveColumnTypeDatabase, column.Name, declared, error))
                : SqlEvaluationException.UnknownType(declared, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnSubject, column.Name));
        }

        if (type.Storage.Type != column.Type &&
            !(type.Storage.Type == DatabaseType.Character && column.Type == DatabaseType.String))
        {
            throw new DatabaseException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerResolveColumnTypeDatabase2, column.Name, declared, SqlType.NameOf(type.Storage.Type), SqlType.NameOf(column.Type)));
        }

        domain = type.IsDomain ? type.Name : null;
        return type.Storage;
    }

    /// <summary>
    /// Binds a compiled schema's types to the engine's type catalog, as the DDL that applies it will,
    /// without touching a database: every domain the schema declares is the engine's (the build adds
    /// a declared database's), and every column's declared type resolves to a type stored as its
    /// member is. Phase 3 of the engine builder's build runs it before any file is touched, and an
    /// imperative apply before any step runs.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="types">The engine's type catalog.</param>
    /// <exception cref="DatabaseException">A domain or a column's type does not bind; the message names it.</exception>
    internal static void BindDeclaredTypes(SqlCompiledSchema schema, SqlTypeCatalog types)
    {
        foreach (CompiledSchemaType declared in schema.Types)
        {
            if (!types.TryGetDomain(declared.Name, out var registered) || registered.Storage.Type != declared.StorageType ||
                registered.Storage.Precision != declared.Precision || registered.Storage.Scale != declared.Scale)
            {
                throw new DatabaseException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerBindDeclaredTypesDatabase, declared.Name, SqlType.ForStorage(new DatabaseTypeInfo(declared.StorageType, null, declared.Precision, declared.Scale)).Name));
            }
        }

        foreach (CompiledSchemaTable table in schema.Tables)
        {
            foreach (CompiledSchemaColumn column in table.Columns)
            {
                try
                {
                    ResolveColumnType(column, types, out _);
                }
                catch (DatabaseException exception)
                {
                    throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerBindDeclaredTypesDatabase2, table.Name, exception.Message), exception.Diagnostics, exception);
                }
            }
        }
    }

    /// <summary>
    /// Validates every DEFAULT against its column's effective type and bounds, and binds every
    /// CHECK a compiled schema declares to the engine's function catalog and its table's
    /// declared columns, as the DDL that applies it will, without touching a database: the text must
    /// be exactly one predicate, every column and function must resolve, every call must match an
    /// overload, and every function must be <see cref="SqlFunctionVolatility.Immutable"/> (owner
    /// decision 64 of 2026-10-09). Phase 3 of the engine builder's build runs it before any file is
    /// touched, and an imperative apply before any step runs.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="defaultCollation">The collation of the database the schema is for.</param>
    /// <param name="functions">The engine's function catalog and the database.</param>
    /// <param name="parserOptions">The engine's parser options.</param>
    /// <param name="validateQualifiers">True for a new or changed schema hash; false before the catalog is known or when the hash was already applied.</param>
    /// <exception cref="DatabaseException">A declared DEFAULT or CHECK does not bind; the message names its table and column or constraint.</exception>
    internal static void BindDeclaredChecks(SqlCompiledSchema schema, Collation defaultCollation, SqlFunctionEnvironment functions,
        SqlQueryParserOptions parserOptions, bool validateQualifiers = true)
    {
        foreach (CompiledSchemaTable table in schema.Tables)
        {
            foreach (CompiledSchemaColumn column in table.Columns)
            {
                try
                {
                    if (CanonicalDefault(column.DefaultLiteral) is { } literal)
                    {
                        var boundColumn = new SqlCatalogColumn(column.Name,
                            ResolveColumnType(column, functions.Types, out string? domain), column.IsNullable,
                            literal, domainName: domain);
                        // Use the DDL path before any storage exists: domain base types and
                        // numeric/length bounds must accept exactly the same literal here.
                        SqlPlanExecutor.ResolveDefault(boundColumn, SqlPersistedExpression.LoadDefaultValue(literal,
                            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QualifiedColumnDefaultSubject, table.Name, column.Name)));
                    }
                }
                catch (DatabaseException exception)
                {
                    throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerBindDeclaredChecksDatabase, table.Name, column.Name, exception.Message),
                        SqlExecutionException.DiagnosticsOf(exception, discardLocations: true), exception);
                }
            }

            SqlCatalogTable? shape = null;
            foreach (var index in table.Indexes.Where(index => index.Predicate is not null))
            {
                shape ??= new SqlCatalogTable(0, SqlPlanner.DefaultSchema, table.Name, table.Columns
                    .Select(column => new SqlCatalogColumn(column.Name,
                        ResolveColumnType(column, functions.Types, out string? domain), column.IsNullable, domainName: domain)).ToArray());
                var predicate = SqlPersistedExpression.ParseDeclaration(index.Predicate!, parserOptions);
                SqlPartialIndexPredicate.Declare(shape, index.Name, index.Columns, index.IsUnique, predicate, defaultCollation, functions);
            }
            foreach (CompiledSchemaConstraint constraint in table.Constraints)
            {
                if (constraint.Kind != CompiledSchemaConstraintKind.Check)
                {
                    continue;
                }

                // The table as its CREATE TABLE will publish it: its declared columns and types, no
                // column collation (the database default), and no data.
                shape ??= new SqlCatalogTable(0, SqlPlanner.DefaultSchema, table.Name, table.Columns
                    .Select(column => new SqlCatalogColumn(column.Name,
                        ResolveColumnType(column, functions.Types, out string? domain), column.IsNullable, domainName: domain))
                    .ToArray());
                try
                {
                    var predicate = SqlPersistedExpression.ParseDeclaration(constraint.Expression?.CanonicalText ?? string.Empty, parserOptions);
                    SqlPlanner.ValidateDeclaredCalls(predicate, functions.Catalog);
                    SqlPlanExecutor.ValidateCheck(predicate, shape, defaultCollation, functions, validateQualifiers: validateQualifiers);
                }
                catch (DatabaseException exception)
                {
                    string reason = exception.Message.EndsWith('.') ? exception.Message : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerBindDeclaredChecksDatabase3, exception.Message);
                    throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerBindDeclaredChecksDatabase2, constraint.Name, table.Name, reason),
                        SqlExecutionException.DiagnosticsOf(exception, discardLocations: true), exception);
                }
            }
        }
    }

    // The identity is cached on the immutable declaration. Generation equality proves that
    // no committed catalog change happened since that identity was recorded.
    private bool IsApplied(SqlCatalogSchemaState? recorded, SqlCompiledSchema schema)
        => recorded is not null && _catalog.PendingMigration is null &&
            string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal) &&
            recorded.Generation == _catalog.DdlGeneration;

    // Names where a provisioning message happened: the engine and the database.
    private string Describe() => string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EngineDatabaseContext, _database.Engine.Name, _database.Name);

    private static string Describe(SqlSchemaMigrationOperation operation)
        => operation.ParentName is null
            ? $"{operation.Kind} '{operation.ObjectName}'"
            : $"{operation.Kind} '{operation.ParentName}.{operation.ObjectName}'";

    private SqlCompiledSchema? ReadCurrentSchema(
        SqlCatalogSchemaState? recorded,
        SqlCompiledSchema desired)
    {
        if (recorded is not null && recorded.Generation == _catalog.DdlGeneration)
        {
            try
            {
                SqlCompiledSchema persisted = SqlCompiledSchemaSerializer.Deserialize(recorded.CanonicalDocument);
                if (string.Equals(recorded.ContentHash, persisted.Hash, StringComparison.Ordinal))
                {
                    return persisted;
                }
            }
            catch (Exception exception) when (
                exception is DatabaseException or JsonException or ArgumentException or NotSupportedException)
            {
                // A stale or malformed state document is not an authority over the live catalog.
                // Reconstruct the SQL subset below so a retry can reconcile partial application.
            }
        }

        return SnapshotCatalog(desired);
    }

    private bool IsScriptObject(string key) => _catalog.Scripts.Any(script => script.Objects.Contains(key, StringComparer.OrdinalIgnoreCase));

    private SqlCompiledSchema? SnapshotCatalog(SqlCompiledSchema desired)
    {
        CatalogWalkCount++;
        IReadOnlyList<SqlCatalogTable> catalogTables = _catalog.Tables
            .Where(table => IsOwnedBy(table.Owner, table.OwningSchema, desired.Name) && !IsScriptObject(SqlObjectIdentity.Table(table.Schema, table.Name)))
            .ToList();
        if (catalogTables.Count == 0 && desired.Types.Count == 0)
        {
            return null;
        }

        var desiredTables = desired.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var tables = new List<CompiledSchemaTable>(catalogTables.Count);

        foreach (SqlCatalogTable catalogTable in catalogTables.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            if (!string.Equals(catalogTable.Schema, SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlSchemaMigrationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerSnapshotCatalogFailure, Describe(), catalogTable.Schema, catalogTable.Name));
            }

            desiredTables.TryGetValue(catalogTable.Name, out CompiledSchemaTable? desiredTable);
            var columns = new List<CompiledSchemaColumn>(catalogTable.Columns.Count);
            foreach (SqlCatalogColumn column in catalogTable.Columns)
            {
                columns.Add(SnapshotColumn(column, desiredTable));
            }
            CompiledSchemaKey? primaryKey = null;
            if (catalogTable.PrimaryKeyColumns.Count > 0)
            {
                string keyName = desiredTable?.PrimaryKey is not null &&
                    NamesEqual(desiredTable.PrimaryKey.Columns, catalogTable.PrimaryKeyColumns)
                        ? desiredTable.PrimaryKey.Name
                        : $"pk_{catalogTable.Name}";
                primaryKey = new CompiledSchemaKey(keyName, catalogTable.PrimaryKeyColumns);
            }

            IReadOnlyList<SqlCatalogIndex> catalogIndexes = _catalog.GetIndexes(catalogTable.ObjectId);
            var indexes = catalogIndexes
                .Where(index => !index.IsPrimaryKey && IsOwnedBy(index.Owner, index.OwningSchema, desired.Name) && !IsScriptObject(SqlObjectIdentity.Index(catalogTable.Schema, catalogTable.Name, index.Name)))
                .OrderBy(index => index.Name, StringComparer.Ordinal)
                .Select(index => new CompiledSchemaIndex(index.Name, index.ColumnNames, index.IsUnique, DeclaredIndexPredicate(desiredTable, index), index.KeyOrders.Select(order => (CompiledSchemaIndexKeyOrder)order).ToArray()))
                .ToList();
            tables.Add(new CompiledSchemaTable(
                catalogTable.Name,
                desiredTable?.RowType ?? $"catalog:{catalogTable.Name}",
                columns,
                primaryKey,
                indexes,
                catalogTable.Constraints.Select(constraint => new CompiledSchemaConstraint(
                    constraint.Name,
                    constraint.Kind == SqlCatalogConstraintKind.Reference ? CompiledSchemaConstraintKind.Reference : CompiledSchemaConstraintKind.Check,
                    constraint.Columns, constraint.ReferencedTable, constraint.ReferencedColumns,
                    constraint.CheckExpression is null ? null : new CompiledSchemaExpression(DeclaredCheckText(desiredTable, constraint)),
                    CompiledDeleteAction(constraint.OnDelete), CompiledUpdateAction(constraint.OnUpdate))).ToArray()));
        }

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            desired.Name,
            allowsDestructiveChanges: false,
            // E3 domains are already registered on the engine before provisioning. They
            // have no database DDL or durable catalog record to replay after a crash.
            desired.Types,
            tables,
            Array.Empty<CompiledSchemaPrincipal>());
    }

    /// <summary>
    /// Reconstructs a live column's compiled form: the desired schema's own declaration when it
    /// declares the column with a SQL type that resolves to the live column's type and domain, so
    /// reconciliation plans no change for it, and otherwise the live type with its domain's name.
    /// </summary>
    private CompiledSchemaColumn SnapshotColumn(SqlCatalogColumn column, CompiledSchemaTable? desiredTable)
    {
        if (column.Identity is not null)
        {
            throw new SqlSchemaMigrationException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.IdentitySchemaOwnedForbidden, SqlProvisioningCodes.PolicyRefused, Describe(), column.Name));
        }
        CompiledSchemaColumn? declared = desiredTable?.Columns.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, column.Name, StringComparison.OrdinalIgnoreCase));
        string? defaultLiteral = declared is not null &&
            string.Equals(CanonicalDefault(declared.DefaultLiteral), column.DefaultLiteral, StringComparison.Ordinal)
                ? declared.DefaultLiteral : column.DefaultLiteral;
        bool nullable = declared is not null &&
            column.IsNullable == (declared.IsNullable && !IsPrimaryKeyColumn(desiredTable!.PrimaryKey, column.Name))
                ? declared.IsNullable : column.IsNullable;
        if (declared is not null && DescribeTypeDifference(declared, column) is null)
        {
            return new CompiledSchemaColumn(column.Name, declared.Type, nullable, declared.MaxLength, declared.Precision,
                declared.Scale, declared.CustomType, defaultLiteral);
        }

        return new CompiledSchemaColumn(column.Name, column.Type.Type, column.IsNullable, column.Type.MaxLength, column.Type.Precision,
            column.Type.Scale, column.DomainName, defaultLiteral);
    }

    /// <summary>
    /// Compares the live schema-owned catalog with a schema and describes the first object that
    /// differs, or returns null when the catalog matches it: final apply validation and the
    /// reason a Verify drift names, so the developer need not diff the catalog by hand.
    /// </summary>
    /// <param name="schema">The schema the catalog should hold.</param>
    /// <returns>The first difference, described for a message, or null.</returns>
    private string? FirstCatalogDifference(SqlCompiledSchema schema)
    {
        CatalogWalkCount++;
        var ownedTables = _catalog.Tables
            .Where(table => IsOwnedBy(table.Owner, table.OwningSchema, schema.Name) && !IsScriptObject(SqlObjectIdentity.Table(table.Schema, table.Name)))
            .ToList();
        foreach (CompiledSchemaTable expected in schema.Tables)
        {
            if (!_catalog.TryGetTable(SqlPlanner.DefaultSchema, expected.Name, out SqlCatalogTable actual))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredTableMissing, expected.Name);
            }

            if (!IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableSchemaOwnershipMismatch, expected.Name, schema.Name);
            }

            if (DescribeColumnDifference(expected, actual) is { } column)
            {
                return column;
            }

            if (!NamesEqual(expected.PrimaryKey?.Columns ?? Array.Empty<string>(), actual.PrimaryKeyColumns))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PrimaryKeyActualContext, expected.Name, string.Join(", ", actual.PrimaryKeyColumns)) +
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PrimaryKeyDeclaredContext, string.Join(", ", expected.PrimaryKey?.Columns ?? Array.Empty<string>()));
            }

            foreach (CompiledSchemaConstraint constraint in expected.Constraints)
            {
                SqlCatalogConstraint? persisted = actual.Constraints.FirstOrDefault(value =>
                    string.Equals(value.Name, constraint.Name, StringComparison.OrdinalIgnoreCase));
                if (persisted is null)
                {
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredConstraintMissing, expected.Name, constraint.Name);
                }

                if ((persisted.Kind == SqlCatalogConstraintKind.Reference) != (constraint.Kind == CompiledSchemaConstraintKind.Reference) ||
                    (constraint.Kind == CompiledSchemaConstraintKind.Reference && !NamesEqual(persisted.Columns, constraint.Columns)) ||
                    !NamesEqual(persisted.ReferencedColumns, constraint.ReferencedColumns) ||
                    !string.Equals(persisted.ReferencedTable, constraint.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    (persisted.Kind == SqlCatalogConstraintKind.Reference && !string.Equals(persisted.ReferencedSchema, SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase)) ||
                    CompiledDeleteAction(persisted.OnDelete) != constraint.OnDelete ||
                    CompiledUpdateAction(persisted.OnUpdate) != constraint.OnUpdate ||
                    !string.Equals(persisted.CheckExpression, CanonicalCheck(constraint.Expression?.CanonicalText), StringComparison.Ordinal))
                {
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintDeclarationMismatch, expected.Name, constraint.Name);
                }
            }

            if (actual.Constraints.Count != expected.Constraints.Count)
            {
                SqlCatalogConstraint? extra = actual.Constraints.FirstOrDefault(value => !expected.Constraints.Any(declared =>
                    string.Equals(declared.Name, value.Name, StringComparison.OrdinalIgnoreCase)));
                return extra is null
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintCountMismatch, expected.Name, actual.Constraints.Count, expected.Constraints.Count)
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExtraConstraint, expected.Name, extra.Name);
            }

            IReadOnlyList<SqlCatalogIndex> actualIndexes = _catalog.GetIndexes(actual.ObjectId)
                .Where(index => !index.IsPrimaryKey && IsOwnedBy(index.Owner, index.OwningSchema, schema.Name) && !IsScriptObject(SqlObjectIdentity.Index(actual.Schema, actual.Name, index.Name)))
                .ToList();
            foreach (CompiledSchemaIndex expectedIndex in expected.Indexes)
            {
                SqlCatalogIndex? actualIndex = actualIndexes.FirstOrDefault(
                    index => string.Equals(index.Name, expectedIndex.Name, StringComparison.OrdinalIgnoreCase));
                if (actualIndex is null)
                {
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredIndexMissing, expected.Name, expectedIndex.Name);
                }

                if (actualIndex.IsUnique != expectedIndex.IsUnique ||
                    !actualIndex.KeyOrders.Select(order => (CompiledSchemaIndexKeyOrder)order).SequenceEqual(expectedIndex.KeyOrders) ||
                    !string.Equals(actualIndex.Predicate, expectedIndex.Predicate is null ? null : SqlPersistedExpression.Canonicalize(
                        SqlPersistedExpression.Load(expectedIndex.Predicate, expectedIndex.Name), expectedIndex.Name), StringComparison.Ordinal) ||
                    !NamesEqual(expectedIndex.Columns, actualIndex.ColumnNames))
                {
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IndexDeclarationMismatch, expected.Name, expectedIndex.Name);
                }
            }

            if (actualIndexes.Count != expected.Indexes.Count)
            {
                SqlCatalogIndex? extra = actualIndexes.FirstOrDefault(index => !expected.Indexes.Any(declared =>
                    string.Equals(declared.Name, index.Name, StringComparison.OrdinalIgnoreCase)));
                return extra is null
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaIndexCountMismatch, expected.Name, actualIndexes.Count, expected.Indexes.Count)
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExtraIndex, expected.Name, extra.Name);
            }
        }

        if (ownedTables.Count != schema.Tables.Count)
        {
            SqlCatalogTable? extra = ownedTables.FirstOrDefault(table => !schema.Tables.Any(declared =>
                string.Equals(declared.Name, table.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(table.Schema, SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase)));
            return extra is null
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OwnedTableCountMismatch, ownedTables.Count, schema.Name, schema.Tables.Count)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExtraOwnedTable, extra.Schema, extra.Name, schema.Name);
        }

        return null;
    }

    // The first column of a table that differs from its declaration, described for a message.
    private string? DescribeColumnDifference(CompiledSchemaTable expected, SqlCatalogTable actual)
    {
        if (actual.Columns.Count != expected.Columns.Count)
        {
            CompiledSchemaColumn? missing = expected.Columns.FirstOrDefault(column => !actual.Columns.Any(live =>
                string.Equals(live.Name, column.Name, StringComparison.OrdinalIgnoreCase)));
            if (missing is not null)
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredColumnMissing, expected.Name, missing.Name);
            }

            SqlCatalogColumn? extra = actual.Columns.FirstOrDefault(column => !expected.Columns.Any(declared =>
                string.Equals(declared.Name, column.Name, StringComparison.OrdinalIgnoreCase)));
            return extra is null
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnCountMismatch, expected.Name, actual.Columns.Count, expected.Columns.Count)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExtraColumn, expected.Name, extra.Name);
        }

        for (int index = 0; index < expected.Columns.Count; index++)
        {
            CompiledSchemaColumn expectedColumn = expected.Columns[index];
            SqlCatalogColumn actualColumn = actual.Columns[index];
            bool expectedIsNullable = expectedColumn.IsNullable &&
                !IsPrimaryKeyColumn(expected.PrimaryKey, expectedColumn.Name);
            if (!string.Equals(expectedColumn.Name, actualColumn.Name, StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnOrderMismatch, expected.Name, index + 1, actualColumn.Name, expectedColumn.Name);
            }

            if (DescribeTypeDifference(expectedColumn, actualColumn) is { } type)
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnDifferenceContext, expected.Name, expectedColumn.Name, type);
            }

            if (expectedIsNullable != actualColumn.IsNullable)
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnTypeContext, expected.Name, expectedColumn.Name) +
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclarationComparison, (actualColumn.IsNullable ? ErrorMessages.NullableColumnDescription : ErrorMessages.NonNullableColumnDescription), (expectedIsNullable ? ErrorMessages.NullableColumnDescription : ErrorMessages.NonNullableColumnDescription));
            }

            if (actualColumn.Identity is not null)
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnIdentityUndeclared, expected.Name, expectedColumn.Name);
            }

            if (!string.Equals(actualColumn.DefaultLiteral, CanonicalDefault(expectedColumn.DefaultLiteral), StringComparison.Ordinal))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnDefaultMismatch, expected.Name, expectedColumn.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// How a live column's type differs from a compiled column's declared type, its domain included
    /// (phase E3), described after the column's name; null when they agree.
    /// </summary>
    private string? DescribeTypeDifference(CompiledSchemaColumn expected, SqlCatalogColumn actual)
    {
        DatabaseTypeInfo type;
        string? domain;
        try
        {
            type = ResolveColumnType(expected, _database.Definitions.Functions.Types, out domain);
        }
        catch (DatabaseException exception)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredTypeUnresolvedReason, exception.Message);
        }

        if (type.Type != actual.Type.Type || type.MaxLength != actual.Type.MaxLength ||
            type.Precision != actual.Type.Precision || type.Scale != actual.Type.Scale)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ActualTypeContext, DescribeType(actual.Type.Type, actual.Type.MaxLength, actual.Type.Precision, actual.Type.Scale)) +
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredTypeContext, DescribeType(type.Type, type.MaxLength, type.Precision, type.Scale));
        }

        return string.Equals(domain, actual.DomainName, StringComparison.OrdinalIgnoreCase)
            ? null
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ActualTypeContext, (actual.DomainName is null ? ErrorMessages.NoActualDomain : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ActualDomainContext, actual.DomainName))) +
              string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredTypeContext, (domain is null ? ErrorMessages.NoDeclaredDomain : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ActualDomainContext, domain)));
    }

    private static string DescribeType(DatabaseType type, int? maxLength, int? precision, int? scale)
        => $"{type}(MaxLength={maxLength?.ToString(CultureInfo.InvariantCulture) ?? "null"}, " +
            $"Precision={precision?.ToString(CultureInfo.InvariantCulture) ?? "null"}, " +
            $"Scale={scale?.ToString(CultureInfo.InvariantCulture) ?? "null"})";

    /// <summary>
    /// The catalog stores a CHECK as the canonical text of its parsed predicate, while a compiled
    /// schema carries the predicate as its author wrote it. Comparing the canonical forms keeps a
    /// re-applied schema a no-op however its predicates are spelled. Text that does not parse
    /// has no canonical form and therefore matches nothing.
    /// </summary>
    private static string? CanonicalCheck(string? declared)
    {
        if (declared is null)
        {
            return null;
        }

        try
        {
            return SqlPersistedExpression.Canonicalize(SqlPersistedExpression.Load(declared, ErrorMessages.CompiledCheckSubject), ErrorMessages.CompiledCheckSubject);
        }
        catch (DatabaseException)
        {
            // A thread out of stack is not text that does not parse: its
            // InsufficientExecutionStackException propagates instead of reading as a changed predicate.
            return null;
        }
    }

    // DEFAULT NULL is stored as absence by the DDL planner. Every other supported
    // DEFAULT is one literal, rendered from its parsed node rather than pasted SQL.
    internal static string? CanonicalDefault(string? declared)
    {
        if (declared is null)
        {
            return null;
        }

        var expression = SqlPersistedExpression.ParseDeclaration(declared, new SqlQueryParserOptions());
        SqlSequenceExecution.RefusePersisted(expression);
        if (!SqlPersistedExpression.IsDefaultLiteral(expression))
        {
            throw new DatabaseException(ErrorMessages.ProvisionerCanonicalDefaultDatabase);
        }

        return expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null }
            ? null
            : SqlPersistedExpression.Canonicalize(expression, ErrorMessages.CompiledDefaultSubject);
    }

    /// <summary>
    /// Reconstructs a live CHECK's compiled text: the desired schema's own spelling when it is
    /// the same predicate, so reconciliation plans no change for it, and otherwise the canonical
    /// text the catalog holds.
    /// </summary>
    private static string? DeclaredIndexPredicate(CompiledSchemaTable? desiredTable, SqlCatalogIndex index)
    {
        string? declared = desiredTable?.Indexes.FirstOrDefault(candidate => candidate.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase))?.Predicate;
        return declared is not null && string.Equals(CanonicalCheck(declared), index.Predicate, StringComparison.Ordinal) ? declared : index.Predicate;
    }

    private static string DeclaredCheckText(CompiledSchemaTable? desiredTable, SqlCatalogConstraint constraint)
    {
        var declared = desiredTable?.Constraints.FirstOrDefault(candidate =>
            candidate.Kind == CompiledSchemaConstraintKind.Check &&
            string.Equals(candidate.Name, constraint.Name, StringComparison.OrdinalIgnoreCase))?.Expression?.CanonicalText;
        return declared is not null && string.Equals(CanonicalCheck(declared), constraint.CheckExpression, StringComparison.Ordinal)
            ? declared
            : constraint.CheckExpression!;
    }

    private void ValidateCatalogOwnership(SqlCompiledSchema schema)
    {
        CatalogWalkCount++;
        foreach (CompiledSchemaTable table in schema.Tables)
        {
            if (!_catalog.TryGetTable(SqlPlanner.DefaultSchema, table.Name, out SqlCatalogTable actual))
            {
                continue;
            }

            if (!IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name))
            {
                throw new SqlSchemaMigrationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateCatalogOwnershipFailure, SqlProvisioningCodes.PolicyRefused, Describe(), schema.Name, table.Name));
            }

            foreach (CompiledSchemaIndex index in table.Indexes)
            {
                if (_catalog.TryGetIndex(actual.ObjectId, index.Name, out SqlCatalogIndex existing) &&
                    !IsOwnedBy(existing.Owner, existing.OwningSchema, schema.Name))
                {
                    throw new SqlSchemaMigrationException(
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateCatalogOwnershipFailure2, SqlProvisioningCodes.PolicyRefused, Describe(), schema.Name, index.Name));
                }
            }
        }
    }

    private static bool IsOwnedBy(DatabaseObjectOwner owner, string? owningSchema, string expectedSchema)
        => owner == DatabaseObjectOwner.Schema &&
            string.Equals(owningSchema, expectedSchema, StringComparison.OrdinalIgnoreCase);

    private static bool IsPrimaryKeyColumn(CompiledSchemaKey? key, string columnName)
    {
        if (key is null)
        {
            return false;
        }

        foreach (string keyColumn in key.Columns)
        {
            if (string.Equals(keyColumn, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ValidateSupportedSchema(SqlCompiledSchema schema)
    {
        bool validateQualifiers = !string.Equals(_catalog.SchemaState?.ContentHash, schema.Hash, StringComparison.Ordinal);
        if (string.Equals(_validatedHash, schema.Hash, StringComparison.Ordinal) && (!validateQualifiers || _validatedQualifiers))
        {
            return;
        }

        if (!string.Equals(schema.Name, _database.Name.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateSupportedSchemaFailure, Describe(), schema.Name));
        }

        if (DescribeUnsupported(schema) is { } unsupported)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateSupportedSchemaFailure2, Describe(), schema.Name, unsupported));
        }

        // Phase 3 bound all declaration rules except qualifier identity. An already-applied
        // hash keeps the older qualifier contract, just like its stored CHECK predicates.
        // A new or changed hash must pass the strict rules before any migration step runs.
        if (!validateQualifiers && _database.Engine.FindDeclaration(_database.Name) is { Schema: { } declared } &&
            string.Equals(declared.Hash, schema.Hash, StringComparison.Ordinal))
        {
            _validatedHash = schema.Hash;
            _validatedQualifiers = false;
            return;
        }

        try
        {
            BindDeclaredTypes(schema, _database.Definitions.Functions.Types);
            BindDeclaredChecks(schema, _catalog.DefaultCollation, _database.Definitions.Functions, _database.Engine.ParserOptions, validateQualifiers);
            _validatedHash = schema.Hash;
            _validatedQualifiers = validateQualifiers;
        }
        catch (DatabaseException invalid)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateSupportedSchemaFailure3, SqlProvisioningCodes.PolicyRefused, Describe(), schema.Name, invalid.Message),
                invalid);
        }
    }

    private static bool NamesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask<CompensationResult> CompensateAsync(
        SqlDatabaseSession session,
        IReadOnlyList<SqlMigrationScriptStep> applied)
    {
        bool complete = true;
        var failures = new List<Exception>();

        for (int index = applied.Count - 1; index >= 0; index--)
        {
            SqlMigrationScriptStep step = applied[index];
            if (step.RollbackRequest is null)
            {
                complete = false;
                continue;
            }

            try
            {
                await session.ExecuteAsync(step.RollbackRequest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                complete = false;
                failures.Add(exception);
            }
        }

        return new CompensationResult(complete, failures);
    }

    private sealed record CompensationResult(bool IsComplete, IReadOnlyList<Exception> Failures);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSchemaProvisioner value)
        {
            _ = value;
        }
    }
}
