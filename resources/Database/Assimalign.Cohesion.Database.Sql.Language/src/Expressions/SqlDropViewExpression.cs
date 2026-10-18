using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents DROP VIEW [IF EXISTS] with RESTRICT or CASCADE.</summary>
public sealed class SqlDropViewExpression : SqlQueryExpression
{
    internal SqlDropViewExpression(SqlTableReference view, bool ifExists, bool cascade, Location location)
        : base(SqlQueryCommandType.Drop, null, location)
    { View = view; IfExists = ifExists; Cascade = cascade; }

    /// <summary>Gets the view name and optional SQL namespace.</summary>
    public SqlTableReference View { get; }
    /// <summary>Gets whether an absent view is accepted.</summary>
    public bool IfExists { get; }
    /// <summary>Gets whether dependent views are removed.</summary>
    public bool Cascade { get; }
}
