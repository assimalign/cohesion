using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a parsed domain DROP statement.</summary>
public sealed class SqlDropDomainExpression : SqlQueryExpression
{
    internal SqlDropDomainExpression(string name, Location location)
        : base(SqlQueryCommandType.Drop, null, location)
    {
        Name = name;
    }

    /// <summary>Gets the domain name.</summary>
    public string Name { get; }
}
