using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Bound unique-index arbiters and the target-plus-EXCLUDED update expressions.</summary>
internal sealed record SqlOnConflictPlan(IReadOnlyList<SqlCatalogIndex> Arbiters, IReadOnlyList<SqlCatalogIndex> UniqueIndexes, bool DoUpdate,
    IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments, SqlBoundExpression? Where);
