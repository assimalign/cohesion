using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static SqlInsertPlan InsertPlan(SqlCatalogTable table, IReadOnlyList<int> ordinals,
        IReadOnlyList<SqlBoundExpression[]> rows, SqlIdentityOverride overriding,
        IReadOnlyList<SqlProjection>? returning, SqlOnConflictPlan? conflict)
        => overriding == SqlIdentityOverride.None ? new SqlInsertPlan(table, ordinals, rows) { Returning = returning, OnConflict = conflict }
            : new SqlIdentityInsertPlan(table, ordinals, rows, overriding) { Returning = returning, OnConflict = conflict };

    private static void ValidateIgnoredIdentityInput(SqlCatalogColumn target, DatabaseType type,
        SqlBoundExpression? value, SqlExpression? expression)
    {
        if (!CanAssignInsertType(type, target.Type.Type))
        { throw new SqlPlanningException("CS02003", string.Format(System.Globalization.CultureInfo.CurrentCulture,
            ErrorMessages.IdentityOverrideAssignmentType, type, target.Name, target.Type.Type), expression?.Location); }
        // Untyped string literals need input conversion during binding (PostgreSQL
        // parse_coerce.c:233-249); BindIgnoredIdentityInput also checks nested CASTs
        // and constant folds. Typed numeric conversions and volatile expressions
        // are discarded without execution; NULL is replaced before NOT NULL checks.
        if (expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String } &&
            value is SqlBoundConstant { Value: not null } constant)
        { _ = SqlPlanExecutor.CoerceForColumn(constant.Value, target); }
    }

    private void RecordIdentityUsage(SqlCatalogTable table)
    {
        for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
        {
            RecordColumnSequenceUsage(table, table.Columns[ordinal]);
        }
    }

    private void RecordColumnSequenceUsage(SqlCatalogTable table, SqlCatalogColumn column)
    {
        for (int index = 0; index < column.DefaultFunctionCalls.Count; index++)
        {
            string dependency = column.DefaultFunctionCalls[index];
            if (dependency.StartsWith("S:", StringComparison.Ordinal) && ulong.TryParse(dependency.AsSpan(2), out ulong id))
            { (SequenceUsage ??= new()).AddIdentity(id); }
        }
        if (column.Identity is null) { return; }
        if (!_catalog.TryGetTable(table.Schema, table.Name, out var current) || !ReferenceEquals(current, table))
        { return; }
        (SequenceUsage ??= new()).AddIdentity(_catalog.IdentitySequence(table.ObjectId, column.Name).ObjectId);
    }

    private void RecordReferentialSequenceUsage(SqlCatalogTable table, bool deleting = true)
    {
        if (!_catalog.HasSequenceDefaults || !_catalog.HasIncomingReferences(table, changingUpdates: !deleting)) { return; }
        var visited = new HashSet<(ulong, bool)> { (table.ObjectId, deleting) };
        var pending = new Queue<(SqlCatalogTable Table, bool Deleting)>();
        pending.Enqueue((table, deleting));
        while (pending.TryDequeue(out var entry))
        {
            foreach (var child in _catalog.Tables)
            {
                foreach (var constraint in child.Constraints)
                {
                    if (constraint.Kind != SqlCatalogConstraintKind.Reference ||
                        !string.Equals(constraint.ReferencedSchema, entry.Table.Schema, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(constraint.ReferencedTable, entry.Table.Name, StringComparison.OrdinalIgnoreCase)) { continue; }
                    var action = entry.Deleting ? constraint.OnDelete : constraint.OnUpdate;
                    if (action == SqlCatalogReferentialAction.SetDefault)
                    { foreach (string name in constraint.Columns) { RecordColumnSequenceUsage(child, child.FindColumn(name)!); } }
                    if (action is SqlCatalogReferentialAction.Cascade or SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault)
                    {
                        bool deleteChild = entry.Deleting && action == SqlCatalogReferentialAction.Cascade;
                        if (visited.Add((child.ObjectId, deleteChild))) { pending.Enqueue((child, deleteChild)); }
                    }
                }
            }
        }
    }

    private static SqlCatalogIdentity? ResolveIdentity(SqlColumnDefinition definition, DatabaseTypeInfo type, bool hasIdentity)
    {
        if (definition.Identity is not { } identity) { return null; }
        if (type.Type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64) || definition.ResolvedType is null)
        {
            throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.EngineIdentityIntegerRequired);
        }
        if (hasIdentity) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityAlreadyDeclared); }
        if (identity.Increment == 0) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityZeroIncrement); }
        if (definition.DefaultValue is not null) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityDefaultConflict); }
        var (minimum, maximum) = IdentityTypeBounds(type.Type, identity.Increment);
        if (identity.Options.Count == 0 && (identity.Start < minimum || identity.Start > maximum))
        {
            throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityStartOutOfRange);
        }
        try
        {
            var options = SqlPlanExecutor.SequenceOptions(identity.Options, null, type.Type, defaultCache: 1);
            return new SqlCatalogIdentity(identity.IsAlways, options.Start, options.Increment) { Options = options };
        }
        catch (SqlCatalogException failure) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, failure.Message); }
    }

    internal static (long Minimum, long Maximum) IdentityTypeBounds(DatabaseType type, long increment)
    {
        // PostgreSQL sequence.c:1462,1494: default bounds depend on generation direction.
        var (minimum, maximum) = type switch
        {
            DatabaseType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
            DatabaseType.Int16 => (short.MinValue, short.MaxValue),
            DatabaseType.Int32 => (int.MinValue, int.MaxValue),
            DatabaseType.Int64 => (long.MinValue, long.MaxValue),
            _ => throw new InvalidOperationException(),
        };
        return increment > 0 ? (1, maximum) : (minimum, -1);
    }
}
