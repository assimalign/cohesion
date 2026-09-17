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
/// Diffs, renders, applies, and records the compiled schema owned by one SQL database.
/// </summary>
internal sealed class SqlSchemaProvisioner
{
    private readonly SqlDatabaseInstance _database;
    private readonly ISqlCatalog _catalog;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    internal SqlSchemaProvisioner(SqlDatabaseInstance database, ISqlCatalog catalog)
    {
        _database = database;
        _catalog = catalog;
    }

    internal async ValueTask<SchemaMigrationResult> ApplyAsync(
        CompiledSchema compiledSchema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compiledSchema);
        if (compiledSchema is not SqlCompiledSchema schema)
        {
            throw new SqlSchemaMigrationException(
                $"SQL database '{_database.Name}' requires a SQL compiled schema, but received model '{compiledSchema.Model}'.");
        }

        ValidateSupportedSchema(schema);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCatalogOwnership(schema);
            SqlCatalogSchemaState? recorded = _catalog.SchemaState;
            string targetHash = schema.Hash;
            string canonicalDocument = schema.CanonicalDocument;
            if (recorded is not null &&
                string.Equals(recorded.ContentHash, targetHash, StringComparison.Ordinal) &&
                string.Equals(recorded.CanonicalDocument, canonicalDocument, StringComparison.Ordinal) &&
                CatalogMatches(schema))
            {
                return new SchemaMigrationResult(recorded.ContentHash, targetHash, 0, WasAlreadyApplied: true);
            }

            SqlCompiledSchema? current = ReadCurrentSchema(recorded, schema);
            SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(current, schema);
            SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(plan, current);
            var applied = new List<SqlMigrationScriptStep>(script.Steps.Count);

            await using IDatabaseSession session = _database.CreateSchemaSession(schema.Name, cancellationToken);
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
                throw new SqlSchemaMigrationException(
                    $"Applying SQL schema '{schema.Name}' failed after {applied.Count} of {script.Steps.Count} operation(s). {state}",
                    inner);
            }

            return new SchemaMigrationResult(
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
                .Where(index => IsOwnedBy(index.Owner, index.OwningSchema, desired.Name))
                .OrderBy(index => index.Name, StringComparer.Ordinal)
                .Select(index => new CompiledSchemaIndex(index.Name, index.ColumnNames, index.IsUnique))
                .ToList();
            tables.Add(new CompiledSchemaTable(
                catalogTable.Name,
                desiredTable?.RowType ?? $"catalog:{catalogTable.Name}",
                columns,
                primaryKey,
                indexes,
                Array.Empty<CompiledSchemaConstraint>()));
        }

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            desired.Name,
            EngineModel.Sql,
            allowsDestructiveChanges: false,
            Array.Empty<CompiledSchemaType>(),
            tables,
            Array.Empty<CompiledSchemaFunction>(),
            Array.Empty<CompiledSchemaTrigger>(),
            Array.Empty<CompiledSchemaPrincipal>(),
            Array.Empty<CompiledSchemaExtension>());
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

            IReadOnlyList<SqlCatalogIndex> actualIndexes = _catalog.GetIndexes(actual.ObjectId)
                .Where(index => IsOwnedBy(index.Owner, index.OwningSchema, schema.Name))
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
                    $"SQL schema '{schema.Name}' cannot adopt table '{table.Name}' because it was not created by this schema.");
            }

            foreach (CompiledSchemaIndex index in table.Indexes)
            {
                if (_catalog.TryGetIndex(actual.ObjectId, index.Name, out SqlCatalogIndex existing) &&
                    !IsOwnedBy(existing.Owner, existing.OwningSchema, schema.Name))
                {
                    throw new SqlSchemaMigrationException(
                        $"SQL schema '{schema.Name}' cannot adopt index '{index.Name}' because it was not created by this schema.");
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
        if (schema.Model != EngineModel.Sql)
        {
            throw new SqlSchemaMigrationException(
                $"SQL database '{_database.Name}' cannot apply a schema for model '{schema.Model}'.");
        }

        if (!string.Equals(schema.Name, _database.Name.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlSchemaMigrationException(
                $"SQL database '{_database.Name}' cannot apply schema '{schema.Name}'.");
        }

        RejectUnsupported(schema.Types.Count, "custom types");
        RejectUnsupported(schema.Functions.Count, "functions");
        RejectUnsupported(schema.Triggers.Count, "triggers");
        RejectUnsupported(schema.Principals.Count, "principals and grants");
        RejectUnsupported(schema.Extensions.Count, "model extensions");

        foreach (CompiledSchemaTable table in schema.Tables)
        {
            if (table.Constraints.Count > 0)
            {
                throw new SqlSchemaMigrationException(
                    $"SQL table '{table.Name}' declares constraints, but the SQL DDL executor " +
                    "does not support foreign-key or check-constraint migrations yet.");
            }

            foreach (CompiledSchemaColumn column in table.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.CustomType))
                {
                    throw new SqlSchemaMigrationException(
                        $"SQL column '{table.Name}.{column.Name}' uses custom type '{column.CustomType}', " +
                        "but custom-type migrations are not supported yet.");
                }
            }
        }

        void RejectUnsupported(int count, string kind)
        {
            if (count > 0)
            {
                throw new SqlSchemaMigrationException(
                    $"SQL schema '{schema.Name}' declares {kind}, but the SQL DDL executor cannot migrate them yet.");
            }
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
        IDatabaseSession session,
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
