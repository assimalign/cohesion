using System;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static SqlCatalogIdentity? ResolveIdentity(SqlColumnDefinition definition, DatabaseTypeInfo type, bool hasIdentity)
    {
        if (definition.Identity is not { } identity) { return null; }
        if (type.Type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64) || definition.ResolvedType is null)
        {
            throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityIntegerRequired);
        }
        if (hasIdentity) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityAlreadyDeclared); }
        if (identity.Increment == 0) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityZeroIncrement); }
        if (definition.DefaultValue is not null) { throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityDefaultConflict); }
        var (minimum, maximum) = IdentityTypeBounds(type.Type);
        if (identity.Start < minimum || identity.Start > maximum)
        {
            throw SqlPlanningException.IdentityViolation(definition.ColumnName, ErrorMessages.IdentityStartOutOfRange);
        }
        return new SqlCatalogIdentity(identity.IsAlways, identity.Start, identity.Increment);
    }

    internal static (long Minimum, long Maximum) IdentityTypeBounds(DatabaseType type) => type switch
    {
        DatabaseType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
        DatabaseType.Int16 => (short.MinValue, short.MaxValue),
        DatabaseType.Int32 => (int.MinValue, int.MaxValue),
        DatabaseType.Int64 => (long.MinValue, long.MaxValue),
        _ => throw new InvalidOperationException(),
    };
}
