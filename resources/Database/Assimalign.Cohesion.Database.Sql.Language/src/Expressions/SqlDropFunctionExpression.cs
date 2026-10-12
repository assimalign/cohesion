using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a parsed function DROP statement.</summary>
public sealed class SqlDropFunctionExpression : SqlQueryExpression
{
    internal SqlDropFunctionExpression(string name, IReadOnlyList<string> parameterTypes, Location location)
        : base(SqlQueryCommandType.Drop, null, location)
    {
        Name = name;
        ParameterTypes = Array.AsReadOnly(parameterTypes.ToArray());
    }

    /// <summary>Gets the function name.</summary>
    public string Name { get; }

    /// <summary>Gets the ordered parameter types.</summary>
    public IReadOnlyList<string> ParameterTypes { get; }
}
