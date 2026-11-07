using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private bool RefusesRollback(SqlSchemaMigrationOperation operation, HashSet<string> validatingTables,
        HashSet<SqlSchemaMigrationOperation> validatingIndexes)
        => operation.Safety != SqlSchemaMigrationSafety.Safe ||
            operation.Kind is SqlSchemaMigrationOperationKind.AddColumn or SqlSchemaMigrationOperationKind.AddTable or SqlSchemaMigrationOperationKind.AddConstraint ||
            operation.Kind == SqlSchemaMigrationOperationKind.AddIndex && (operation.Index!.IsUnique || validatingIndexes.Contains(operation)) ||
            operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn && (RequiresValueValidation(operation) ||
                validatingTables.Contains(operation.ParentName!) && RewritesRollbackValues(operation));

    private HashSet<string> RollbackValidationTables(SqlCompiledSchema schema)
    {
        var tables = schema.Tables.Where(table => table.Constraints.Count > 0 || table.Columns.Any(column =>
        {
            if (column.CustomType is null) { return false; }
            _ = ResolveColumnType(column, _database.Engine.Types, out var domain);
            return domain is not null;
        })).Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        tables.UnionWith(schema.Tables.SelectMany(table => table.Constraints)
            .Where(constraint => constraint.Kind == CompiledSchemaConstraintKind.Reference)
            .Select(constraint => constraint.ReferencedObject!));
        // Incoming references on retained ad-hoc tables also validate a replaced parent.
        var declared = schema.Tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in _catalog.Tables.Where(table =>
            !table.Schema.Equals(SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase) || !declared.Contains(table.Name)))
        {
            tables.UnionWith(table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                string.Equals(constraint.ReferencedSchema, SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase))
                .Select(constraint => constraint.ReferencedTable!));
        }
        return tables;
    }

    private HashSet<SqlSchemaMigrationOperation> RollbackIndexValidations(SqlSchemaMigrationPlan plan, HashSet<string> validatingTables)
    {
        var refused = new HashSet<SqlSchemaMigrationOperation>();
        foreach (var added in plan.Operations.Where(operation => operation.Kind == SqlSchemaMigrationOperationKind.AddIndex &&
            !operation.Index!.IsUnique && validatingTables.Contains(operation.ParentName!)))
        {
            if (!plan.Operations.Any(operation => operation.Kind == SqlSchemaMigrationOperationKind.DropIndex &&
                string.Equals(operation.ParentName, added.ParentName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(operation.ObjectName, added.ObjectName, StringComparison.OrdinalIgnoreCase)) ||
                !_catalog.TryGetTable(SqlPlanner.DefaultSchema, added.ParentName!, out var table)) { continue; }
            // Match the joint planner's owner decision: replacing a same-name heap-owned
            // index copies the table and re-evaluates retained row/domain/FK constraints.
            if (_database.IndexManager.ExportRegistrations().Any(registration => registration.ObjectId == table.ObjectId &&
                registration.Definition.Name.Equals(added.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                _database.IndexManager.PageOwner(registration) == table.ObjectId)) { refused.Add(added); }
        }
        return refused;
    }

    private bool RewritesRollbackValues(SqlSchemaMigrationOperation operation)
    {
        // A rewrite rebinds and evaluates retained CHECK/FK metadata too. Exact storage
        // widening alone cannot prove its predicates accept the newer version's rows.
        var previous = ResolveColumnType(operation.PreviousColumn!, _database.Engine.Types, out var previousDomain);
        var desired = ResolveColumnType(operation.Column!, _database.Engine.Types, out var desiredDomain);
        return SqlMigrationScriptGenerator.RequiresTypeRewrite(previous, previousDomain, desired, desiredDomain);
    }

    private string DescribeHeldSchema(SqlCompiledSchema schema, SqlCatalogSchemaState? stored,
        IReadOnlyList<SqlSchemaMigrationOperation> refused, bool rollback)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessages.SchemaHeld,
            Describe(), schema.Hash, stored?.ContentHash ?? "(none)",
            rollback ? ErrorMessages.RollbackSchemaHeld : ErrorMessages.NewSchemaHeld,
            string.Join("; ", refused.Select(Describe)));

    private SqlSchemaApplyResult HoldSchema(SqlCompiledSchema schema, SqlCatalogSchemaState? stored,
        SqlSchemaMigrationPlan plan, IReadOnlyList<SqlSchemaMigrationOperation> refused, bool rollback)
    {
        string code = rollback ? SqlProvisioningCodes.RollbackHeld : SqlProvisioningCodes.PolicyRefused;
        string detail = DescribeHeldSchema(schema, stored, refused, rollback);
        var progress = new DatabaseProvisioningProgress("SchemaHeld", schema.Name, 1,
            plan.Operations.Count, false, failureCode: code, detail: detail);
        _database.ReportProvisioning(progress);
        TraceStage("SchemaHeld", code, stepCount: plan.Operations.Count);
        TraceOutcome("Held");
        return new SqlSchemaApplyResult(new(stored?.ContentHash, schema.Hash, 0, WasAlreadyApplied: false), progress);
    }
}
