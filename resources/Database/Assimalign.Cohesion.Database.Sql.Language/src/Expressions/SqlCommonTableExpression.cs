using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A named query in a statement's WITH list.</summary>
public sealed class SqlCommonTableExpression
{
    internal SqlCommonTableExpression(string name, IReadOnlyList<string> columns, SqlQueryBodyExpression body, Location location, bool isRecursiveScope = false)
    {
        IsRecursiveScope = isRecursiveScope;
        Name = name;
        Columns = columns;
        Body = body;
        Location = location;
    }

    /// <summary>Gets whether the containing WITH list declares RECURSIVE visibility.</summary>
    public bool IsRecursiveScope { get; }

    /// <summary>Gets the relation name, without a schema qualifier.</summary>
    public string Name { get; }

    /// <summary>Gets the output column names, or an empty list when omitted.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the query body, with declaration-order visibility or all WITH entries in a RECURSIVE scope.</summary>
    public SqlQueryBodyExpression Body { get; }

    /// <summary>Gets the definition's source span.</summary>
    public Location Location { get; }
}
