using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlJointMigrationPlan(SqlJointMigrationTable[] Tables, SqlCatalogTable[] Dropped,
    SqlCatalogTable[] Touched);

internal sealed record SqlJointMigrationTable(SqlCatalogTable? Previous, SqlCatalogTable Shape,
    SqlCatalogIndex[] Indexes, SqlBoundExpression?[] Values);
