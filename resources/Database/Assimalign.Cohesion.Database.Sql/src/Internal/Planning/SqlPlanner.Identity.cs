using System;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private void RecordIdentityUsage(SqlCatalogTable table)
    {
        for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
        {
            var column = table.Columns[ordinal];
            for (int index = 0; index < column.DefaultFunctionCalls.Count; index++)
            {
                string dependency = column.DefaultFunctionCalls[index];
                if (dependency.StartsWith("S:", StringComparison.Ordinal) && ulong.TryParse(dependency.AsSpan(2), out ulong id))
                { (SequenceUsage ??= new()).Names.Add(id); }
            }
            if (column.Identity is null) { continue; }
            if (!_catalog.TryGetTable(table.Schema, table.Name, out var current) || !ReferenceEquals(current, table))
            { return; }
            (SequenceUsage ??= new()).Names.Add(_catalog.IdentitySequence(table.ObjectId, column.Name).ObjectId);
        }
    }

    private void RecordReferentialSequenceUsage(SqlCatalogTable table)
    {
        if (!_catalog.HasIncomingReferences(table)) { return; }
        var visited = new System.Collections.Generic.HashSet<ulong> { table.ObjectId };
        var pending = new System.Collections.Generic.Queue<SqlCatalogTable>();
        pending.Enqueue(table);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in _catalog.Tables)
            {
                if (!child.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    string.Equals(constraint.ReferencedSchema, parent.Schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(constraint.ReferencedTable, parent.Name, StringComparison.OrdinalIgnoreCase))) { continue; }
                if (visited.Add(child.ObjectId)) { RecordIdentityUsage(child); pending.Enqueue(child); }
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
