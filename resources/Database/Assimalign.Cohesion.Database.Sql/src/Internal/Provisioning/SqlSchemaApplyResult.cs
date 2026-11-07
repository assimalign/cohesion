using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal readonly record struct SqlSchemaApplyResult(
    SqlSchemaMigrationResult Migration,
    DatabaseProvisioningProgress? HeldProgress = null)
{
    internal bool IsHeld => HeldProgress is not null;
}
