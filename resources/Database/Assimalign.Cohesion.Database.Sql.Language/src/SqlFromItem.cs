using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>One comma-separated FROM item: a relation followed by its left-deep JOIN tree.</summary>
public sealed class SqlFromItem
{
    internal SqlFromItem(SqlTableReference table, IReadOnlyList<SqlJoinClause> joins)
    {
        Table = table;
        Joins = joins;
    }

    /// <summary>Gets the first relation in this item.</summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the joins in written order. Their ON scopes exclude other comma items.</summary>
    public IReadOnlyList<SqlJoinClause> Joins { get; }
}
