using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

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
        => GenerateCore(plan, currentSchema, joint: false);

    // The engine can prove a rewrite from a native domain's live facets, which
    // portable declarations alone cannot resolve. Keep that decision authoritative.
    internal static SqlMigrationScript GenerateJoint(SqlSchemaMigrationPlan plan, SqlCompiledSchema? currentSchema)
        => GenerateCore(plan, currentSchema, joint: true);

    private static SqlMigrationScript GenerateCore(SqlSchemaMigrationPlan plan, SqlCompiledSchema? currentSchema, bool joint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (currentSchema is not null &&
            !string.Equals(plan.SourceHash, currentSchema.Hash, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ScriptGeneratorGenerateFailure, plan.SourceHash ?? "<empty>", currentSchema.Hash));
        }

        // A default change needs publication of the complete target. Preserve the base
        // generator's refusal when no operation in the plan requires a TYPE rewrite.
        joint = joint || plan.Operations.Any(operation => RenderChecked(operation, () =>
        {
            if (operation.Kind != SqlSchemaMigrationOperationKind.AlterColumn ||
                operation.Column is not { } column || operation.PreviousColumn is not { } previous) { return false; }
            var type = ResolveAlterColumnType(column, out string? domain);
            var old = ResolveAlterColumnType(previous, out string? oldDomain);
            return RequiresTypeRewrite(old, oldDomain, type, domain);
        }));
        var steps = new List<SqlMigrationScriptStep>(plan.Operations.Count);

        foreach (SqlSchemaMigrationOperation operation in plan.Operations)
        {
            // A domain is registered on the engine, not stored in the database (phase E3): adding or
            // dropping one changes no catalog, so it renders no statement. The engine that declares the
            // database registers the schema's domains when it builds.
            if (operation.Kind is SqlSchemaMigrationOperationKind.AddDomain or SqlSchemaMigrationOperationKind.DropDomain)
            {
                continue;
            }

            if (operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn)
            { RenderChecked(operation, () => { RenderAlterColumns(operation, steps, joint); return true; }); }
            else { steps.Add(Render(operation, currentSchema)); }
        }

        return new SqlMigrationScript(plan, steps);
    }

    private static T RenderChecked<T>(SqlSchemaMigrationOperation operation, Func<T> render)
    {
        try { return render(); }
        catch (SqlSchemaMigrationException) { throw; }
        catch (Exception exception) when (exception is DatabaseException or ArgumentException)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MigrationScriptGeneratorRenderFailure,
                    operation.Kind, QualifiedName(operation), exception.Message), exception);
        }
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

                SqlSchemaMigrationOperationKind.AddConstraint => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.AddConstraint(RequireParent(operation), RequireConstraint(operation)),
                    SqlSchemaStatementRenderer.DropConstraint(RequireParent(operation), operation.ObjectName)),

                SqlSchemaMigrationOperationKind.DropConstraint => new SqlMigrationScriptStep(
                    operation,
                    SqlSchemaStatementRenderer.DropConstraint(RequireParent(operation), operation.ObjectName),
                    operation.Constraint is null ? null : SqlSchemaStatementRenderer.AddConstraint(
                        RequireParent(operation), RequireConstraint(operation))),

                SqlSchemaMigrationOperationKind.AlterColumn => RenderAlterColumn(operation),

                SqlSchemaMigrationOperationKind.AlterTable =>
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
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MigrationScriptGeneratorRenderFailure, operation.Kind, QualifiedName(operation), exception.Message),
                exception);
        }
    }

    private static void RenderAlterColumns(SqlSchemaMigrationOperation operation, List<SqlMigrationScriptStep> steps, bool joint)
    {
        if (operation.Column is not { } column || operation.PreviousColumn is not { } previous)
        {
            throw InvalidPayload(operation, "the previous and desired column definitions");
        }
        RequireMatchingName(operation, column.Name, "column");
        DatabaseTypeInfo type = ResolveAlterColumnType(column, out string? domain);
        DatabaseTypeInfo previousType = ResolveAlterColumnType(previous, out string? previousDomain);
        bool changesDefault = !string.Equals(SqlSchemaProvisioner.CanonicalDefault(column.DefaultLiteral),
            SqlSchemaProvisioner.CanonicalDefault(previous.DefaultLiteral), StringComparison.Ordinal);
        if (changesDefault && !joint)
        {
            throw Unsupported(operation, "ALTER metadata requiring a storage-type, domain, scale or default rewrite is not implemented");
        }
        string table = RequireParent(operation);
        bool changesType = type.Type != previousType.Type || type.MaxLength != previousType.MaxLength ||
            type.Precision != previousType.Precision || type.Scale != previousType.Scale ||
            !string.Equals(domain, previousDomain, StringComparison.OrdinalIgnoreCase);
        bool rewrite = RequiresTypeRewrite(previousType, previousDomain, type, domain);
        if (!rewrite && !changesDefault)
        {
            steps.Add(RenderAlterColumn(operation));
            return;
        }

        // A portable AlterColumn already carries both typed definitions. Lower its TYPE
        // before any nullability change; both retain their place in declaration order.
        // Reverse conversion can lose data even when an assignment cast exists, so
        // TYPE has no compensating SQL. Joint execution consumes the complete plan.
        var typed = column with { IsNullable = previous.IsNullable, DefaultLiteral = null };
        if (rewrite)
        {
            // The complete declaration supplies the final default. Remove the old
            // override before TYPE so an obsolete expression cannot block conversion.
            if (previous.DefaultLiteral is not null)
            {
                var drop = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                    operation.ObjectName, operation.ParentName, column: previous with { DefaultLiteral = null }, previousColumn: previous);
                steps.Add(new SqlMigrationScriptStep(drop,
                    SqlSchemaStatementRenderer.SetDefault(table, previous with { DefaultLiteral = null }),
                    SqlSchemaStatementRenderer.SetDefault(table, previous)));
            }
            var typeOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: typed, previousColumn: previous);
            steps.Add(new SqlMigrationScriptStep(typeOperation,
                SqlSchemaStatementRenderer.AlterColumnType(table, column), rollbackStatementText: null));
        }
        else if (changesType)
        {
            // Preserve 49a's reversible full-definition lowering for compatible
            // facets, including when a separate default change is also requested.
            typed = column with { IsNullable = previous.IsNullable, DefaultLiteral = previous.DefaultLiteral };
            steps.Add(RenderAlterColumn(new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: typed, previousColumn: previous)));
        }
        if (changesDefault || rewrite && column.DefaultLiteral is not null)
        {
            // The declaration's explicit default is authoritative afterwards (for example 1.234 declared
            // on NUMERIC(8,2) is stored as that expression, while TYPE folds it to 1.23).
            // A separate existing DDL action keeps the exported statement sequence
            // faithful to that declaration. Joint publication includes the final default.
            var defaultOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: column with { IsNullable = previous.IsNullable }, previousColumn: typed);
            steps.Add(new SqlMigrationScriptStep(defaultOperation, SqlSchemaStatementRenderer.SetDefault(table, column),
                rewrite ? null : SqlSchemaStatementRenderer.SetDefault(table, previous)));
        }
        if (previous.IsNullable != column.IsNullable)
        {
            var nullableOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: column, previousColumn: column with { IsNullable = previous.IsNullable });
            steps.Add(new SqlMigrationScriptStep(nullableOperation,
                SqlSchemaStatementRenderer.SetNullability(table, column),
                SqlSchemaStatementRenderer.SetNullability(table, typed)));
        }
    }

    // Precision and widening length bounds retain 49a's validation-only
    // path. They must not turn a non-TYPE migration into a physical rewrite.
    internal static bool RequiresTypeRewrite(DatabaseTypeInfo previous, string? previousDomain, DatabaseTypeInfo type, string? domain)
        => type.Type != previous.Type || type.Scale != previous.Scale ||
            !string.Equals(domain, previousDomain, StringComparison.OrdinalIgnoreCase) ||
            type.Type == DatabaseType.Character && type.MaxLength != previous.MaxLength ||
            type.Type == DatabaseType.String && type.MaxLength is { } length &&
                (previous.MaxLength is null || length < previous.MaxLength);

    private static SqlMigrationScriptStep RenderAlterColumn(SqlSchemaMigrationOperation operation)
        => new(operation, SqlSchemaStatementRenderer.AlterColumn(RequireParent(operation), operation.Column!),
            SqlSchemaStatementRenderer.AlterColumn(RequireParent(operation), operation.PreviousColumn!));

    private static DatabaseTypeInfo ResolveAlterColumnType(CompiledSchemaColumn column, out string? domain)
    {
        if (column.CustomType is not { } declared || SqlTypeCatalog.Standard.Resolve(declared, out _) is not null)
        {
            return SqlSchemaProvisioner.ResolveColumnType(column, SqlTypeCatalog.Standard, out domain);
        }

        // Script generation has no engine catalog. An engine-owned domain is compared by
        // identity; its immutable bounds are validated when provisioning binds the schema.
        if (!SqlTypeRules.TrySplit(declared, out string name, out int? first, out _) || first is not null)
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ResolveAlterColumnTypeDatabase, column.Name, declared));
        }
        SqlTypeRules.ValidateDomainName(name);
        domain = name;
        return new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision, column.Scale);
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

    private static CompiledSchemaConstraint RequireConstraint(SqlSchemaMigrationOperation operation)
    {
        CompiledSchemaConstraint constraint = operation.Constraint ?? throw InvalidPayload(operation, "a constraint definition");
        RequireMatchingName(operation, constraint.Name, "constraint");
        return constraint;
    }

    private static CompiledSchemaColumn RequireColumn(SqlSchemaMigrationOperation operation)
    {
        CompiledSchemaColumn column = operation.Column ??
            throw InvalidPayload(operation, "a desired column definition");
        RequireMatchingName(operation, column.Name, "column");

        if (!column.IsNullable && SqlSchemaProvisioner.CanonicalDefault(column.DefaultLiteral) is null)
        {
            throw Unsupported(
                operation,
                "adding a non-nullable column requires a non-null literal default or a backfill");
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
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GeneratorRequireMatchingNameFailure, operation.Kind, operation.ObjectName, payloadKind, payloadName));
        }
    }

    private static string RequireParent(SqlSchemaMigrationOperation operation)
        => !string.IsNullOrWhiteSpace(operation.ParentName)
            ? operation.ParentName
            : throw InvalidPayload(operation, "an owning table name");

    private static SqlSchemaMigrationException InvalidPayload(
        SqlSchemaMigrationOperation operation,
        string expected)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ScriptGeneratorInvalidPayloadFailure, operation.Kind, QualifiedName(operation), expected));

    private static SqlSchemaMigrationException Unsupported(
        SqlSchemaMigrationOperation operation,
        string reason)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ScriptGeneratorUnsupportedFailure, operation.Kind, QualifiedName(operation), reason));

    private static string QualifiedName(SqlSchemaMigrationOperation operation)
        => operation.ParentName is null
            ? operation.ObjectName
            : $"{operation.ParentName}.{operation.ObjectName}";
}
