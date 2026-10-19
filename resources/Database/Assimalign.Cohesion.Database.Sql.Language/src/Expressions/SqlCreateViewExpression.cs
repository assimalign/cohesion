using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents CREATE [OR REPLACE] VIEW.</summary>
public sealed class SqlCreateViewExpression : SqlQueryExpression
{
    internal SqlCreateViewExpression(SqlTableReference view, IReadOnlyList<string> columns, SqlQueryBodyExpression query, bool orReplace, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    { View = view; Columns = columns; Query = query; OrReplace = orReplace; }

    /// <summary>Gets the view name and optional SQL namespace.</summary>
    public SqlTableReference View { get; }
    /// <summary>Gets the optional exact output column list.</summary>
    public IReadOnlyList<string> Columns { get; }
    /// <summary>Gets the query defining the view.</summary>
    public SqlQueryBodyExpression Query { get; }
    /// <summary>Gets whether an existing view may be replaced.</summary>
    public bool OrReplace { get; }
}
