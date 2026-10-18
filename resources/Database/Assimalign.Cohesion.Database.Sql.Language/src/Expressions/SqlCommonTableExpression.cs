using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A named, non-recursive query in a statement's WITH list.</summary>
public sealed class SqlCommonTableExpression
{
    internal SqlCommonTableExpression(string name, IReadOnlyList<string> columns, SqlSelectExpression body, Location location)
    {
        Name = name;
        Columns = columns;
        Body = body;
        Location = location;
    }

    /// <summary>Gets the relation name, without a schema qualifier.</summary>
    public string Name { get; }

    /// <summary>Gets the output column names, or an empty list when omitted.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the SELECT body, which can see earlier WITH entries.</summary>
    public SqlSelectExpression Body { get; }

    /// <summary>Gets the definition's source span.</summary>
    public Location Location { get; }
}
