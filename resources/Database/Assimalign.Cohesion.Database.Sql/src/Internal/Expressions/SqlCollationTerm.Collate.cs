using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A <c>COLLATE</c> over a deferred operand: an inner explicit collation still wins.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCollationCollateTerm")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationCollateTerm.DebugView))]
internal sealed class SqlCollationCollateTerm : SqlCollationTerm
{
    private readonly SqlCollationCandidate _operand;
    private readonly Collation? _collation;
    private readonly string _name;

    /// <summary>Initializes the term.</summary>
    /// <param name="operand">The operand's candidate.</param>
    /// <param name="collation">
    /// The collation the clause names, resolved when the expression was bound, or null when the
    /// name did not resolve: the parser refuses an unknown name, so only a tree no SQL text spells
    /// has one, and it fails, as before, only when the clause applies.
    /// </param>
    /// <param name="name">The collation name the clause names.</param>
    internal SqlCollationCollateTerm(SqlCollationCandidate operand, Collation? collation, string name)
    {
        _operand = operand;
        _collation = collation;
        _name = name;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
    {
        var inner = _operand.Resolve(subqueries);
        return inner.Priority == 3 ? inner : new SqlCollationCandidate(_collation ?? Collation.FromName(_name), 3);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationCollateTerm value)
        {
            _ = value;
        }
    }
}
