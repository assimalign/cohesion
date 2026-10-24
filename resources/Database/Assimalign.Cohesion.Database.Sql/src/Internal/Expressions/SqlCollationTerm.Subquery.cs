using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An <c>IN (subquery)</c> node: when the subquery returned rows, its values' collation (priority 2);
/// when it returned none, the collation of the IN's operand.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCollationSubqueryTerm")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationSubqueryTerm.DebugView))]
internal sealed class SqlCollationSubqueryTerm : SqlCollationTerm
{
    private readonly int _slot;
    private readonly Collation _collation;
    private readonly SqlCollationCandidate _empty;

    /// <summary>Initializes the term.</summary>
    /// <param name="slot">The subquery's slot in the statement.</param>
    /// <param name="collation">The subquery column's collation, which its values carry.</param>
    /// <param name="empty">The candidate when the subquery returned no rows.</param>
    internal SqlCollationSubqueryTerm(int slot, Collation collation, SqlCollationCandidate empty)
    {
        _slot = slot;
        _collation = collation;
        _empty = empty;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
        => subqueries is not null && subqueries.TryGet(_slot, out var values) && values.Length > 0
            ? new SqlCollationCandidate(_collation, 2)
            : _empty.Resolve(subqueries);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationSubqueryTerm value)
        {
            _ = value;
        }
    }
}
