namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>What decides a deferred <see cref="SqlCollationCandidate"/> when its comparison runs.</summary>
internal abstract class SqlCollationTerm
{
    /// <summary>Resolves the term.</summary>
    /// <param name="subqueries">The statement's materialized subquery values, or null when it has none.</param>
    /// <returns>The fixed candidate.</returns>
    internal abstract SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries);
}
