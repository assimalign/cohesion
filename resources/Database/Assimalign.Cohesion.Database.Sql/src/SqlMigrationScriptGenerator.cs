using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Renders portable schema migration plans into the SQL engine's DDL dialect.</summary>
public static class SqlMigrationScriptGenerator
{
    /// <summary>Renders an ordered migration plan into parsed SQL requests.</summary>
    /// <param name="plan">The portable migration plan.</param>
    /// <param name="currentSchema">
    /// The source compiled schema, when available. It supplies compensation metadata for
    /// operations whose portable plan only needs an object name in the forward direction.
    /// </param>
    /// <returns>The deterministic SQL migration script.</returns>
    /// <exception cref="SqlSchemaMigrationException">
    /// A plan operation cannot be represented by the SQL engine's current DDL dialect.
    /// </exception>
    public static SqlMigrationScript Generate(
        SqlSchemaMigrationPlan plan,
        SqlCompiledSchema? currentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (currentSchema is not null &&
            !string.Equals(plan.SourceHash, currentSchema.Hash, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException(
                $"Migration plan source hash '{plan.SourceHash ?? "<empty>"}' does not match " +
                $"the supplied current schema hash '{currentSchema.Hash}'.");
        }

        var steps = new List<SqlMigrationScriptStep>(plan.Operations.Count);

        foreach (SqlSchemaMigrationOperation operation in plan.Operations)
        {
            steps.Add(Render(operation, currentSchema));
        }

        return new SqlMigrationScript(plan, steps);
    }

    private static SqlMigrationScriptStep Render(
        SqlSchemaMigrationOperation operation,
        SqlCompiledSchema? currentSchema)
    {
        try
        {
            return operation.Kind switch
            {
                SqlSchemaMigrationOperationKind.AddTable => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.CreateTable(RequireTable(operation)),
                    SqlSchemaStatementRenderer.DropTable(operation.ObjectName)),

                SqlSchemaMigrationOperationKind.DropTable => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.DropTable(operation.ObjectName),
                    rollbackStatementText: null),

                SqlSchemaMigrationOperationKind.AddColumn => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.AddColumn(
                        RequireParent(operation),
                        RequireColumn(operation)),
                    SqlSchemaStatementRenderer.DropColumn(
                        RequireParent(operation),
                        operation.ObjectName)),

                SqlSchemaMigrationOperationKind.DropColumn => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.DropColumn(
                        RequireParent(operation),
                        operation.ObjectName),
                    rollbackStatementText: null),

                SqlSchemaMigrationOperationKind.AddIndex => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.CreateIndex(
                        RequireParent(operation),
                        RequireIndex(operation)),
                    SqlSchemaStatementRenderer.DropIndex(
                        RequireParent(operation),
                        operation.ObjectName)),

                SqlSchemaMigrationOperationKind.DropIndex => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.DropIndex(
                        RequireParent(operation),
                        operation.ObjectName),
                    PreviousIndexStatement(operation, currentSchema)),

                SqlSchemaMigrationOperationKind.AlterTable or SqlSchemaMigrationOperationKind.AlterColumn =>
                    throw Unsupported(operation, "ALTER metadata is not implemented by the SQL DDL dialect"),

                _ => throw Unsupported(operation, "the operation kind is unknown"),
            };
        }
        catch (SqlSchemaMigrationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DatabaseException or ArgumentException)
        {
            throw new SqlSchemaMigrationException(
                $"Cannot render SQL migration operation '{operation.Kind}' for '{QualifiedName(operation)}': {exception.Message}",
                exception);
        }
    }

    private static string? PreviousIndexStatement(
        SqlSchemaMigrationOperation operation,
        SqlCompiledSchema? currentSchema)
    {
        if (currentSchema is null)
        {
            return null;
        }

        string tableName = RequireParent(operation);
        foreach (CompiledSchemaTable table in currentSchema.Tables)
        {
            if (!string.Equals(table.Name, tableName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (CompiledSchemaIndex index in table.Indexes)
            {
                if (string.Equals(index.Name, operation.ObjectName, StringComparison.Ordinal))
                {
                    return SqlSchemaStatementRenderer.CreateIndex(tableName, index);
                }
            }
        }

        return null;
    }

    private static CompiledSchemaTable RequireTable(SqlSchemaMigrationOperation operation)
    {
        CompiledSchemaTable table = operation.Table ??
            throw InvalidPayload(operation, "a desired table definition");
        RequireMatchingName(operation, table.Name, "table");
        return table;
    }

    private static CompiledSchemaColumn RequireColumn(SqlSchemaMigrationOperation operation)
    {
        CompiledSchemaColumn column = operation.Column ??
            throw InvalidPayload(operation, "a desired column definition");
        RequireMatchingName(operation, column.Name, "column");

        if (!column.IsNullable)
        {
            throw Unsupported(
                operation,
                "adding a non-nullable column requires a default or backfill, which is not supported");
        }

        return column;
    }

    private static CompiledSchemaIndex RequireIndex(SqlSchemaMigrationOperation operation)
    {
        CompiledSchemaIndex index = operation.Index ??
            throw InvalidPayload(operation, "a desired index definition");
        RequireMatchingName(operation, index.Name, "index");
        return index;
    }

    private static void RequireMatchingName(
        SqlSchemaMigrationOperation operation,
        string payloadName,
        string payloadKind)
    {
        if (!string.Equals(operation.ObjectName, payloadName, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException(
                $"Migration operation '{operation.Kind}' names '{operation.ObjectName}' but carries " +
                $"a {payloadKind} definition named '{payloadName}'.");
        }
    }

    private static string RequireParent(SqlSchemaMigrationOperation operation)
        => !string.IsNullOrWhiteSpace(operation.ParentName)
            ? operation.ParentName
            : throw InvalidPayload(operation, "an owning table name");

    private static SqlSchemaMigrationException InvalidPayload(
        SqlSchemaMigrationOperation operation,
        string expected)
        => new($"Migration operation '{operation.Kind}' for '{QualifiedName(operation)}' does not carry {expected}.");

    private static SqlSchemaMigrationException Unsupported(
        SqlSchemaMigrationOperation operation,
        string reason)
        => new($"SQL migration operation '{operation.Kind}' for '{QualifiedName(operation)}' is unsupported: {reason}.");

    private static string QualifiedName(SqlSchemaMigrationOperation operation)
        => operation.ParentName is null
            ? operation.ObjectName
            : $"{operation.ParentName}.{operation.ObjectName}";
}
