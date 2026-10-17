using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>An aliased SELECT used as a FROM or JOIN relation, with optional output names.</summary>
public sealed class SqlDerivedTableReference : SqlRelationReference
{
    internal SqlDerivedTableReference(SqlSelectExpression query, string alias, IReadOnlyList<string> columns)
        : base(alias)
    {
        Query = query;
        Columns = columns;
    }

    /// <summary>Gets the query that produces this relation's rows.</summary>
    public SqlSelectExpression Query { get; }

    /// <summary>Gets the derived column names, or an empty list when output names are inherited.</summary>
    public IReadOnlyList<string> Columns { get; }
}
