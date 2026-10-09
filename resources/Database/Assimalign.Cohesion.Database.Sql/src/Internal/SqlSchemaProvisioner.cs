using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Catalog;

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
        ValidateSupportedSchema(schema);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCatalogOwnership(schema);
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            string targetHash = schema.Hash;
            string canonicalDocument = schema.CanonicalDocument;
            if (IsApplied(recorded, schema))
            {
                return new SqlSchemaMigrationResult(recorded!.ContentHash, targetHash, 0, WasAlreadyApplied: true);
            }

            SqlCompiledSchema? current = ReadCurrentSchema(recorded, schema);
            SqlSchemaMigrationPlan plan;
            try
            {
                plan = SqlSchemaMigrationPlanner.Plan(current, schema);
            }
            catch (SqlSchemaMigrationException refused)
            {
                // The planner knows the schemas, not where they are applied.
                throw new SqlSchemaMigrationException($"{Describe()}: {refused.Message}", refused);
            }

            SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(plan, current);
            var applied = new List<SqlMigrationScriptStep>(script.Steps.Count);

            await using SqlDatabaseSession session = _database.CreateSchemaSession(schema.Name, cancellationToken);
            try
            {
                foreach (SqlMigrationScriptStep step in script.Steps)
                {
                    await session.ExecuteAsync(step.Request, cancellationToken).ConfigureAwait(false);
                    applied.Add(step);
                }

                await _catalog.SaveSchemaStateAsync(
                    new SqlCatalogSchemaState(targetHash, canonicalDocument),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                CompensationResult compensation = await CompensateAsync(session, applied).ConfigureAwait(false);
                if (failure is OperationCanceledException && compensation.IsComplete)
                {
                    throw;
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
    /// hash and canonical document are the schema's, and the live schema-owned catalog matches it.
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
        ValidateSupportedSchema(schema);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            if (IsApplied(recorded, schema))
            {
                return new SqlSchemaMigrationResult(recorded!.ContentHash, schema.Hash, 0, WasAlreadyApplied: true);
            }

            string reason = recorded is null
                ? "the database records no applied schema"
                : !string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal)
                    ? $"the database records schema hash {recorded.ContentHash}"
                    : "the live catalog no longer matches the recorded schema";
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
    {
        if (schema.Principals.Count > 0)
        {
            return $"principal '{schema.Principals[0].Name}', and principals and grants are refused until their DDL exists";
        }

        if (schema.Types.Count > 0)
        {
            return $"custom type '{schema.Types[0].Name}', and custom types are refused until their DDL exists";
        }

        foreach (CompiledSchemaTable table in schema.Tables)
        {
            foreach (CompiledSchemaColumn column in table.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.CustomType))
                {
                    return $"column '{table.Name}.{column.Name}' of custom type '{column.CustomType}', and custom-type " +
                        "migrations are not supported yet";
                }
            }
        }

        return null;
    }

    // The recorded hash, the recorded canonical document and the live catalog all agree with the schema.
    private bool IsApplied(SqlCatalogSchemaState? recorded, SqlCompiledSchema schema)
        => recorded is not null &&
            string.Equals(recorded.ContentHash, schema.Hash, StringComparison.Ordinal) &&
            string.Equals(recorded.CanonicalDocument, schema.CanonicalDocument, StringComparison.Ordinal) &&
            CatalogMatches(schema);

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
        if (recorded is not null)
        {
            try
            {
                SqlCompiledSchema persisted = SqlCompiledSchemaSerializer.Deserialize(recorded.CanonicalDocument);
                if (string.Equals(recorded.ContentHash, persisted.Hash, StringComparison.Ordinal) &&
                    CatalogMatches(persisted))
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
        IReadOnlyList<SqlCatalogTable> catalogTables = _catalog.Tables
            .Where(table => IsOwnedBy(table.Owner, table.OwningSchema, desired.Name))
            .ToList();
        if (catalogTables.Count == 0)
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
                    $"The live SQL catalog contains table '{catalogTable.Schema}.{catalogTable.Name}', " +
                    "but compiled schemas currently address the 'dbo' schema only.");
            }

            var columns = new List<CompiledSchemaColumn>(catalogTable.Columns.Count);
            foreach (SqlCatalogColumn column in catalogTable.Columns)
            {
                if (column.DefaultLiteral is not null)
                {
                    throw new SqlSchemaMigrationException(
                        $"The live SQL catalog column '{catalogTable.Name}.{column.Name}' has a default literal, " +
                        "which the compiled schema contract cannot represent yet.");
                }

                columns.Add(new CompiledSchemaColumn(
                    column.Name,
                    column.Type.Type,
                    column.IsNullable,
                    column.Type.MaxLength,
                    column.Type.Precision,
                    column.Type.Scale));
            }

            desiredTables.TryGetValue(catalogTable.Name, out CompiledSchemaTable? desiredTable);
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
            Array.Empty<CompiledSchemaType>(),
            tables,
            Array.Empty<CompiledSchemaPrincipal>());
    }

    private bool CatalogMatches(SqlCompiledSchema schema)
    {
        if (_catalog.Tables.Count(table => IsOwnedBy(table.Owner, table.OwningSchema, schema.Name)) != schema.Tables.Count)
        {
            return false;
        }

        foreach (CompiledSchemaTable expected in schema.Tables)
        {
            if (!_catalog.TryGetTable("dbo", expected.Name, out SqlCatalogTable actual) ||
                !IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name) ||
                actual.Columns.Count != expected.Columns.Count ||
                actual.Constraints.Count != expected.Constraints.Count ||
                !NamesEqual(expected.PrimaryKey?.Columns ?? Array.Empty<string>(), actual.PrimaryKeyColumns))
            {
                return false;
            }

            for (int index = 0; index < expected.Columns.Count; index++)
            {
                CompiledSchemaColumn expectedColumn = expected.Columns[index];
                SqlCatalogColumn actualColumn = actual.Columns[index];
                bool expectedIsNullable = expectedColumn.IsNullable &&
                    !IsPrimaryKeyColumn(expected.PrimaryKey, expectedColumn.Name);
                if (!string.Equals(expectedColumn.Name, actualColumn.Name, StringComparison.OrdinalIgnoreCase) ||
                    expectedColumn.Type != actualColumn.Type.Type ||
                    expectedIsNullable != actualColumn.IsNullable ||
                    expectedColumn.MaxLength != actualColumn.Type.MaxLength ||
                    expectedColumn.Precision != actualColumn.Type.Precision ||
                    expectedColumn.Scale != actualColumn.Type.Scale ||
                    expectedColumn.CustomType is not null ||
                    actualColumn.DefaultLiteral is not null)
                {
                    return false;
                }
            }

            foreach (CompiledSchemaConstraint constraint in expected.Constraints)
            {
                SqlCatalogConstraint? persisted = actual.Constraints.FirstOrDefault(value =>
                    string.Equals(value.Name, constraint.Name, StringComparison.OrdinalIgnoreCase));
                if (persisted is null ||
                    (persisted.Kind == SqlCatalogConstraintKind.Reference) != (constraint.Kind == CompiledSchemaConstraintKind.Reference) ||
                    (constraint.Kind == CompiledSchemaConstraintKind.Reference && !NamesEqual(persisted.Columns, constraint.Columns)) ||
                    !NamesEqual(persisted.ReferencedColumns, constraint.ReferencedColumns) ||
                    !string.Equals(persisted.ReferencedTable, constraint.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    (persisted.Kind == SqlCatalogConstraintKind.Reference && !string.Equals(persisted.ReferencedSchema, "dbo", StringComparison.OrdinalIgnoreCase)) ||
                    (persisted.OnDelete == SqlCatalogReferentialAction.Cascade) != (constraint.OnDelete == CompiledSchemaReferentialAction.Cascade) ||
                    !string.Equals(persisted.CheckExpression, CanonicalCheck(constraint.Expression?.CanonicalText), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            IReadOnlyList<SqlCatalogIndex> actualIndexes = _catalog.GetIndexes(actual.ObjectId)
                .Where(index => !index.IsPrimaryKey && IsOwnedBy(index.Owner, index.OwningSchema, schema.Name))
                .ToList();
            if (actualIndexes.Count != expected.Indexes.Count)
            {
                return false;
            }

            foreach (CompiledSchemaIndex expectedIndex in expected.Indexes)
            {
                SqlCatalogIndex? actualIndex = actualIndexes.FirstOrDefault(
                    index => string.Equals(index.Name, expectedIndex.Name, StringComparison.OrdinalIgnoreCase));
                if (actualIndex is null ||
                    actualIndex.IsUnique != expectedIndex.IsUnique ||
                    !NamesEqual(expectedIndex.Columns, actualIndex.ColumnNames))
                {
                    return false;
                }
            }
        }

        return true;
    }

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
        foreach (CompiledSchemaTable table in schema.Tables)
        {
            if (!_catalog.TryGetTable("dbo", table.Name, out SqlCatalogTable actual))
            {
                continue;
            }

            if (!IsOwnedBy(actual.Owner, actual.OwningSchema, schema.Name))
            {
                throw new SqlSchemaMigrationException(
                    $"{Describe()}: SQL schema '{schema.Name}' cannot adopt table '{table.Name}' because it was not created by this schema.");
            }

            foreach (CompiledSchemaIndex index in table.Indexes)
            {
                if (_catalog.TryGetIndex(actual.ObjectId, index.Name, out SqlCatalogIndex existing) &&
                    !IsOwnedBy(existing.Owner, existing.OwningSchema, schema.Name))
                {
                    throw new SqlSchemaMigrationException(
                        $"{Describe()}: SQL schema '{schema.Name}' cannot adopt index '{index.Name}' because it was not created by this schema.");
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
