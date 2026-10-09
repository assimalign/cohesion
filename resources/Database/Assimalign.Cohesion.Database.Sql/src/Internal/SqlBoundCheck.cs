using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A persisted CHECK constraint parsed and bound against one table version: the predicate's tree,
/// the bound expression a validated write evaluates, and the ordinals of the columns it reads,
/// which a violation reports.
/// </summary>
/// <param name="Constraint">The catalog constraint the predicate was loaded from.</param>
/// <param name="Predicate">The parsed predicate, which DDL that changes the table binds again against the changed columns.</param>
/// <param name="ColumnOrdinals">The distinct column ordinals the predicate references, in first-use order.</param>
/// <param name="Bound">
/// The predicate bound to the table version's columns, once (<see cref="SqlExpressionEvaluator.Bind(SqlExpression)"/>).
/// Every session writing the version evaluates this one tree, which is immutable.
/// </param>
internal sealed record SqlBoundCheck(SqlCatalogConstraint Constraint, SqlExpression Predicate, IReadOnlyList<int> ColumnOrdinals,
    SqlBoundExpression Bound);
