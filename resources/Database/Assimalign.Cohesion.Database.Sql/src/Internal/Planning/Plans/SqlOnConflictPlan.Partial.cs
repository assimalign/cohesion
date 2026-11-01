using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Inference proof exists only for explicitly targeted partial arbiters.</summary>
internal sealed record SqlPartialOnConflictPlan(IReadOnlyList<SqlCatalogIndex> Arbiters,
    IReadOnlyList<SqlCatalogIndex> UniqueIndexes, bool DoUpdate,
    IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments, SqlBoundExpression? Where,
    IReadOnlyList<(SqlCatalogIndex Index, string Proof)> Proofs)
    : SqlOnConflictPlan(Arbiters, UniqueIndexes, DoUpdate, Assignments, Where);
