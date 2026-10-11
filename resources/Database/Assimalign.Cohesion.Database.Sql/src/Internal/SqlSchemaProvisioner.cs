using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Diffs, renders, applies, verifies and records the compiled schema owned by one SQL database:
/// the imperative <see cref="SqlDatabase.ApplySchemaAsync"/> path and phase 6 of the engine
/// builder's build (<see cref="SqlDeclaredDatabase"/>) share it.
/// </summary>
internal sealed class SqlSchemaProvisioner
{
    private readonly SqlDatabase _database;
    private readonly SqlCatalog _catalog;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private string? _validatedHash;

    // Instance-local test hooks, reached only by this assembly's tests. A skip must not
    // enter any catalog traversal; crash tests capture durable storage after each step.
    internal int CatalogWalkCount { get; private set; }
    internal SqlSchemaMigrationPlan? LastPlan { get; private set; }
    internal Action<SqlMigrationScriptStep>? StepApplied { get; set; }

    internal SqlSchemaProvisioner(SqlDatabase database, SqlCatalog catalog)
    {
        _database = database;
        _catalog = catalog;
    }

    internal async ValueTask<SqlSchemaMigrationResult> ApplyAsync(
        SqlCompiledSchema schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateSupportedSchema(schema);
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            string targetHash = schema.Hash;
            if (IsApplied(recorded, schema))
            {
                return new SqlSchemaMigrationResult(recorded!.ContentHash, targetHash, 0, WasAlreadyApplied: true);
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
                    $"{SqlProvisioningCodes.PolicyRefused}: {Describe()}: {refused.Message} Nothing was changed.",
                    refused);
            }

            LastPlan = plan;
            SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(plan, current);
            var applied = new List<SqlMigrationScriptStep>(script.Steps.Count);

            await using SqlDatabaseSession session = _database.CreateSchemaSession(schema.Name, cancellationToken);
            try
            {
                foreach (SqlMigrationScriptStep step in script.Steps)
                {
                    await session.ExecuteAsync(step.Request, cancellationToken).ConfigureAwait(false);
                    applied.Add(step);
                    StepApplied?.Invoke(step);
                }

                // IF NOT EXISTS and concurrent ad-hoc DDL must not turn an unowned or
                // mismatched object into a trusted marker. Capture before validation;
                // SaveSchemaState checks the generation again under the catalog lock.
                ulong generation = _catalog.DdlGeneration;
                if (FirstCatalogDifference(schema) is { } difference)
                {
                    throw new SqlSchemaMigrationException($"{Describe()}: the applied catalog differs: {difference}.");
                }

                await _catalog.SaveSchemaStateAsync(
                    new SqlCatalogSchemaState(targetHash, schema.CanonicalDocument, generation),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                CompensationResult compensation = await CompensateAsync(session, applied).ConfigureAwait(false);

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
                        $"{Describe()}: applying schema '{schema.Name}' was canceled; every completed step was compensated.",
                        failure,
                        cancellationToken);
                }

                string state = compensation.IsComplete
                    ? "Every completed step was compensated."
                    : "Compensation was incomplete; the live catalog must be reconciled before retrying.";
                Exception inner = compensation.Failures.Count == 0
                    ? failure
                    : new AggregateException(new[] { failure }.Concat(compensation.Failures));
                string where = applied.Count < script.Steps.Count
                    ? $"step {applied.Count + 1} of {script.Steps.Count} ({Describe(script.Steps[applied.Count].Operation)}) failed"
                    : $"recording the applied schema failed after all {script.Steps.Count} step(s)";
                throw new SqlSchemaMigrationException(
                    $"{SqlProvisioningCodes.StepFailed}: {Describe()}: applying schema '{schema.Name}': {where}. {state}",
                    inner);
            }

            return new SqlSchemaMigrationResult(
                plan.SourceHash,
                targetHash,
                plan.Operations.Count,
                WasAlreadyApplied: false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Verifies, without running any DDL, that the database holds exactly the schema: the recorded
    /// hash and generation match the declaration and the current catalog generation.
    /// </summary>
    /// <param name="schema">The declared schema.</param>
    /// <param name="cancellationToken">Observed while the apply gate is awaited.</param>
    /// <returns>The result, always already applied.</returns>
    /// <exception cref="SqlSchemaMigrationException">The database drifted (<c>COHSQLP003</c>).</exception>
    internal async ValueTask<SqlSchemaMigrationResult> VerifyAsync(
        SqlCompiledSchema schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateSupportedSchema(schema);
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            if (IsApplied(recorded, schema))
            {
                return new SqlSchemaMigrationResult(recorded!.ContentHash, schema.Hash, 0, WasAlreadyApplied: true);
            }

            string reason = recorded is null
                ? "the database records no applied schema"
                : !string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal)
                    ? $"the database records schema hash {recorded.ContentHash}"
                    : $"the catalog DDL generation changed from {recorded.Generation} to {_catalog.DdlGeneration}";

            // Name the first object that differs, so the developer need not diff the catalog by hand.
            if (FirstCatalogDifference(schema) is { } difference)
            {
                reason += $"; first difference from the declaration: {difference}";
            }

            throw new SqlSchemaMigrationException(
                $"{SqlProvisioningCodes.Drift}: {Describe()}: the declared schema (hash {schema.Hash}) is not the one " +
                $"applied: {reason}. The database is declared in Verify mode, so nothing was changed; migrate it out of " +
                "band, or declare it in Apply mode.");
        }
        finally
        {
            _applyGate.Release();
        }
    }

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
            ? $"principal '{schema.Principals[0].Name}', and principals and grants are refused until their DDL exists"
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
            return new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision, column.Scale);
        }

        if (types.Resolve(declared, out string? error) is not { } type)
        {
            throw error is not null
                ? new DatabaseException($"Column '{column.Name}' is declared with type '{declared}': {error}.")
                : SqlEvaluationException.UnknownType(declared, $"Column '{column.Name}'");
        }

        if (type.Storage.Type != column.Type)
        {
            throw new DatabaseException(
                $"Column '{column.Name}' is declared with type '{declared}', which is stored as {SqlType.NameOf(type.Storage.Type)}, " +
                $"but its member is stored as {SqlType.NameOf(column.Type)}; declare a type whose storage is the member's.");
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
                    $"The schema declares domain '{declared.Name}' over {SqlType.ForStorage(new DatabaseTypeInfo(declared.StorageType, null, declared.Precision, declared.Scale)).Name}, " +
                    "which this engine does not register with that base type; declare the database on the engine's builder " +
                    "(SqlDatabaseEngineBuilder.AddDatabase), which registers the domains its schema declares, or register the domain " +
                    "(SqlDatabaseEngineBuilder.Types).");
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
                    throw new DatabaseException($"Table '{table.Name}': {exception.Message}", exception);
                }
            }
        }
    }

    /// <summary>
    /// Binds every CHECK a compiled schema declares to the engine's function catalog and its table's
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
    /// <exception cref="DatabaseException">A declared CHECK does not bind; the message names its table and constraint.</exception>
    internal static void BindDeclaredChecks(SqlCompiledSchema schema, Collation defaultCollation, SqlFunctionEnvironment functions,
        SqlQueryParserOptions parserOptions)
    {
        foreach (CompiledSchemaTable table in schema.Tables)
        {
            foreach (CompiledSchemaColumn column in table.Columns)
            {
                try
                {
                    _ = CanonicalDefault(column.DefaultLiteral);
                }
                catch (DatabaseException exception)
                {
                    throw new DatabaseException($"DEFAULT on column '{table.Name}.{column.Name}' does not bind: {exception.Message}", exception);
                }
            }

            SqlCatalogTable? shape = null;
            foreach (CompiledSchemaConstraint constraint in table.Constraints)
            {
                if (constraint.Kind != CompiledSchemaConstraintKind.Check)
                {
                    continue;
                }

                // The table as its CREATE TABLE will publish it: its declared columns and types, no
                // column collation (the database default), and no data.
                shape ??= new SqlCatalogTable(0, "dbo", table.Name, table.Columns
                    .Select(column => new SqlCatalogColumn(column.Name,
                        ResolveColumnType(column, functions.Types, out string? domain), column.IsNullable, domainName: domain))
                    .ToArray());
                try
                {
                    var predicate = SqlPersistedExpression.ParseDeclaration(constraint.Expression?.CanonicalText ?? string.Empty, parserOptions);
                    SqlPlanner.ValidateDeclaredCalls(predicate, functions.Catalog);
                    SqlPlanExecutor.ValidateCheck(predicate, shape, defaultCollation, functions);
                }
                catch (DatabaseException exception)
                {
                    string reason = exception.Message.EndsWith('.') ? exception.Message : exception.Message + ".";
                    throw new DatabaseException($"CHECK constraint '{constraint.Name}' on table '{table.Name}' does not bind: {reason}", exception);
                }
            }
        }
    }

    // The identity is cached on the immutable declaration. Generation equality proves that
    // no committed catalog change happened since that identity was recorded.
    private bool IsApplied(SqlCatalogSchemaState? recorded, SqlCompiledSchema schema)
        => recorded is not null &&
            string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal) &&
            recorded.Generation == _catalog.DdlGeneration;

    // Names where a provisioning message happened: the engine and the database.
    private string Describe() => $"SQL engine '{_database.Engine.Name}', database '{_database.Name}'";

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

    private SqlCompiledSchema? SnapshotCatalog(SqlCompiledSchema desired)
    {
        CatalogWalkCount++;
        IReadOnlyList<SqlCatalogTable> catalogTables = _catalog.Tables
            .Where(table => IsOwnedBy(table.Owner, table.OwningSchema, desired.Name))
            .ToList();
        if (catalogTables.Count == 0 && desired.Types.Count == 0)
        {
            return null;
        }

        var desiredTables = desired.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var tables = new List<CompiledSchemaTable>(catalogTables.Count);

        foreach (SqlCatalogTable catalogTable in catalogTables.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            if (!string.Equals(catalogTable.Schema, "dbo", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlSchemaMigrationException(
                    $"{Describe()}: the live SQL catalog contains table '{catalogTable.Schema}.{catalogTable.Name}', " +
                    "but compiled schemas currently address the 'dbo' schema only.");
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
                .Where(index => !index.IsPrimaryKey && IsOwnedBy(index.Owner, index.OwningSchema, desired.Name))
                .OrderBy(index => index.Name, StringComparer.Ordinal)
                .Select(index => new CompiledSchemaIndex(index.Name, index.ColumnNames, index.IsUnique))
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
                    constraint.OnDelete == SqlCatalogReferentialAction.Cascade ? CompiledSchemaReferentialAction.Cascade : CompiledSchemaReferentialAction.Restrict)).ToArray()));
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
            .Where(table => IsOwnedBy(table.Owner, table.OwningSchema, schema.Name))
            .ToList();
        foreach (CompiledSchemaTable expected in schema.Tables)
        {
            if (!_catalog.TryGetTable("dbo", expected.Name, out SqlCatalogTable actual))
            {
                return $"table '{expected.Name}' is missing";
            }

            if (!IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name))
            {
                return $"table '{expected.Name}' was not created by schema '{schema.Name}'";
            }

            if (DescribeColumnDifference(expected, actual) is { } column)
            {
                return column;
            }

            if (!NamesEqual(expected.PrimaryKey?.Columns ?? Array.Empty<string>(), actual.PrimaryKeyColumns))
            {
                return $"table '{expected.Name}': its primary key is ({string.Join(", ", actual.PrimaryKeyColumns)}), " +
                    $"declared ({string.Join(", ", expected.PrimaryKey?.Columns ?? Array.Empty<string>())})";
            }

            foreach (CompiledSchemaConstraint constraint in expected.Constraints)
            {
                SqlCatalogConstraint? persisted = actual.Constraints.FirstOrDefault(value =>
                    string.Equals(value.Name, constraint.Name, StringComparison.OrdinalIgnoreCase));
                if (persisted is null)
                {
                    return $"table '{expected.Name}': constraint '{constraint.Name}' is missing";
                }

                if ((persisted.Kind == SqlCatalogConstraintKind.Reference) != (constraint.Kind == CompiledSchemaConstraintKind.Reference) ||
                    (constraint.Kind == CompiledSchemaConstraintKind.Reference && !NamesEqual(persisted.Columns, constraint.Columns)) ||
                    !NamesEqual(persisted.ReferencedColumns, constraint.ReferencedColumns) ||
                    !string.Equals(persisted.ReferencedTable, constraint.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    (persisted.Kind == SqlCatalogConstraintKind.Reference && !string.Equals(persisted.ReferencedSchema, "dbo", StringComparison.OrdinalIgnoreCase)) ||
                    (persisted.OnDelete == SqlCatalogReferentialAction.Cascade) != (constraint.OnDelete == CompiledSchemaReferentialAction.Cascade) ||
                    !string.Equals(persisted.CheckExpression, CanonicalCheck(constraint.Expression?.CanonicalText), StringComparison.Ordinal))
                {
                    return $"table '{expected.Name}': constraint '{constraint.Name}' differs from its declaration";
                }
            }

            if (actual.Constraints.Count != expected.Constraints.Count)
            {
                SqlCatalogConstraint? extra = actual.Constraints.FirstOrDefault(value => !expected.Constraints.Any(declared =>
                    string.Equals(declared.Name, value.Name, StringComparison.OrdinalIgnoreCase)));
                return extra is null
                    ? $"table '{expected.Name}' has {actual.Constraints.Count} constraint(s), declared {expected.Constraints.Count}"
                    : $"table '{expected.Name}': constraint '{extra.Name}' is not declared";
            }

            IReadOnlyList<SqlCatalogIndex> actualIndexes = _catalog.GetIndexes(actual.ObjectId)
                .Where(index => !index.IsPrimaryKey && IsOwnedBy(index.Owner, index.OwningSchema, schema.Name))
                .ToList();
            foreach (CompiledSchemaIndex expectedIndex in expected.Indexes)
            {
                SqlCatalogIndex? actualIndex = actualIndexes.FirstOrDefault(
                    index => string.Equals(index.Name, expectedIndex.Name, StringComparison.OrdinalIgnoreCase));
                if (actualIndex is null)
                {
                    return $"table '{expected.Name}': index '{expectedIndex.Name}' is missing";
                }

                if (actualIndex.IsUnique != expectedIndex.IsUnique ||
                    !NamesEqual(expectedIndex.Columns, actualIndex.ColumnNames))
                {
                    return $"table '{expected.Name}': index '{expectedIndex.Name}' differs from its declaration (uniqueness or columns)";
                }
            }

            if (actualIndexes.Count != expected.Indexes.Count)
            {
                SqlCatalogIndex? extra = actualIndexes.FirstOrDefault(index => !expected.Indexes.Any(declared =>
                    string.Equals(declared.Name, index.Name, StringComparison.OrdinalIgnoreCase)));
                return extra is null
                    ? $"table '{expected.Name}' has {actualIndexes.Count} schema-owned index(es), declared {expected.Indexes.Count}"
                    : $"table '{expected.Name}': index '{extra.Name}' is not declared";
            }
        }

        if (ownedTables.Count != schema.Tables.Count)
        {
            SqlCatalogTable? extra = ownedTables.FirstOrDefault(table => !schema.Tables.Any(declared =>
                string.Equals(declared.Name, table.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase)));
            return extra is null
                ? $"the database has {ownedTables.Count} table(s) created by schema '{schema.Name}', declared {schema.Tables.Count}"
                : $"table '{extra.Schema}.{extra.Name}' was created by schema '{schema.Name}' but is not declared";
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
                return $"table '{expected.Name}': column '{missing.Name}' is missing";
            }

            SqlCatalogColumn? extra = actual.Columns.FirstOrDefault(column => !expected.Columns.Any(declared =>
                string.Equals(declared.Name, column.Name, StringComparison.OrdinalIgnoreCase)));
            return extra is null
                ? $"table '{expected.Name}' has {actual.Columns.Count} column(s), declared {expected.Columns.Count}"
                : $"table '{expected.Name}': column '{extra.Name}' is not declared";
        }

        for (int index = 0; index < expected.Columns.Count; index++)
        {
            CompiledSchemaColumn expectedColumn = expected.Columns[index];
            SqlCatalogColumn actualColumn = actual.Columns[index];
            bool expectedIsNullable = expectedColumn.IsNullable &&
                !IsPrimaryKeyColumn(expected.PrimaryKey, expectedColumn.Name);
            if (!string.Equals(expectedColumn.Name, actualColumn.Name, StringComparison.OrdinalIgnoreCase))
            {
                return $"table '{expected.Name}': column {index + 1} is '{actualColumn.Name}', declared '{expectedColumn.Name}'";
            }

            if (DescribeTypeDifference(expectedColumn, actualColumn) is { } type)
            {
                return $"table '{expected.Name}': column '{expectedColumn.Name}' {type}";
            }

            if (expectedIsNullable != actualColumn.IsNullable)
            {
                return $"table '{expected.Name}': column '{expectedColumn.Name}' is " +
                    $"{(actualColumn.IsNullable ? "nullable" : "not nullable")}, declared {(expectedIsNullable ? "nullable" : "not nullable")}";
            }

            if (!string.Equals(actualColumn.DefaultLiteral, CanonicalDefault(expectedColumn.DefaultLiteral), StringComparison.Ordinal))
            {
                return $"table '{expected.Name}': column '{expectedColumn.Name}' has a different default from its declaration";
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
            return $"is declared with a type this engine does not resolve ({exception.Message})";
        }

        if (type.Type != actual.Type.Type || type.MaxLength != actual.Type.MaxLength ||
            type.Precision != actual.Type.Precision || type.Scale != actual.Type.Scale)
        {
            return $"is {DescribeType(actual.Type.Type, actual.Type.MaxLength, actual.Type.Precision, actual.Type.Scale)}, " +
                $"declared {DescribeType(type.Type, type.MaxLength, type.Precision, type.Scale)}";
        }

        return string.Equals(domain, actual.DomainName, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"is {(actual.DomainName is null ? "of no domain" : $"of domain '{actual.DomainName}'")}, " +
              $"declared {(domain is null ? "with no domain" : $"of domain '{domain}'")}";
    }

    private static string DescribeType(DatabaseType type, int? maxLength, int? precision, int? scale)
        => maxLength is { } length
            ? $"{type}({length})"
            : precision is { } digits
                ? $"{type}({digits},{scale ?? 0})"
                : type.ToString();

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
            return SqlPersistedExpression.Canonicalize(SqlPersistedExpression.Load(declared, "Compiled CHECK predicate"), "Compiled CHECK predicate");
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
        if (expression is not SqlLiteralExpression literal)
        {
            throw new DatabaseException("Only literal DEFAULT values are supported.");
        }

        return literal.LiteralType == SqlLiteralType.Null
            ? null
            : SqlPersistedExpression.Canonicalize(literal, "Compiled column DEFAULT");
    }

    /// <summary>
    /// Reconstructs a live CHECK's compiled text: the desired schema's own spelling when it is
    /// the same predicate, so reconciliation plans no change for it, and otherwise the canonical
    /// text the catalog holds.
    /// </summary>
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
            if (!_catalog.TryGetTable("dbo", table.Name, out SqlCatalogTable actual))
            {
                continue;
            }

            if (!IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name))
            {
                throw new SqlSchemaMigrationException(
                    $"{SqlProvisioningCodes.PolicyRefused}: {Describe()}: SQL schema '{schema.Name}' cannot adopt table '{table.Name}' " +
                    "because it was not created by this schema. Nothing was changed.");
            }

            foreach (CompiledSchemaIndex index in table.Indexes)
            {
                if (_catalog.TryGetIndex(actual.ObjectId, index.Name, out SqlCatalogIndex existing) &&
                    !IsOwnedBy(existing.Owner, existing.OwningSchema, schema.Name))
                {
                    throw new SqlSchemaMigrationException(
                        $"{SqlProvisioningCodes.PolicyRefused}: {Describe()}: SQL schema '{schema.Name}' cannot adopt index '{index.Name}' " +
                        "because it was not created by this schema. Nothing was changed.");
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
        if (string.Equals(_validatedHash, schema.Hash, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(schema.Name, _database.Name.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlSchemaMigrationException(
                $"{Describe()}: the database cannot apply schema '{schema.Name}'.");
        }

        if (DescribeUnsupported(schema) is { } unsupported)
        {
            throw new SqlSchemaMigrationException(
                $"{Describe()}: SQL schema '{schema.Name}' declares {unsupported}.");
        }

        // The checks the engine's build makes before any file is touched, for an apply that did not
        // come through a build: a declared CHECK must bind before any step runs. The schema the
        // engine declares for this database was bound by the build's phase 3, against the same
        // frozen catalog and parser options, and the build refused a database whose collation
        // differs from the declared one, so an equal hash has nothing left to bind.
        if (_database.Engine.FindDeclaration(_database.Name) is { Schema: { } declared } &&
            string.Equals(declared.Hash, schema.Hash, StringComparison.Ordinal))
        {
            _validatedHash = schema.Hash;
            return;
        }

        try
        {
            BindDeclaredTypes(schema, _database.Definitions.Functions.Types);
            BindDeclaredChecks(schema, _catalog.DefaultCollation, _database.Definitions.Functions, _database.Engine.ParserOptions);
            _validatedHash = schema.Hash;
        }
        catch (DatabaseException invalid)
        {
            throw new SqlSchemaMigrationException(
                $"{SqlProvisioningCodes.PolicyRefused}: {Describe()}: SQL schema '{schema.Name}': {invalid.Message} Nothing was changed.",
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
}
