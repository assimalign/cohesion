using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A persisted CHECK constraint parsed and bound against one table version: the predicate a
/// validated write evaluates, and the ordinals of the columns it reads, which a violation
/// reports.
/// </summary>
/// <param name="Constraint">The catalog constraint the predicate was loaded from.</param>
/// <param name="Predicate">The bound predicate.</param>
/// <param name="ColumnOrdinals">The distinct column ordinals the predicate references, in first-use order.</param>
internal sealed record SqlBoundCheck(SqlCatalogConstraint Constraint, SqlExpression Predicate, IReadOnlyList<int> ColumnOrdinals);
