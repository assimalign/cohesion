using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a parsed function CREATE statement.</summary>
public sealed class SqlCreateFunctionExpression : SqlQueryExpression
{
    internal SqlCreateFunctionExpression(string name, IReadOnlyList<SqlFunctionParameter> parameters, string returnType, string volatility, bool isStrict, SqlExpression body, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    {
        Name = name;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        ReturnType = returnType;
        Volatility = volatility;
        IsStrict = isStrict;
        Body = body;
    }

    /// <summary>Gets the function name.</summary>
    public string Name { get; }

    /// <summary>Gets the ordered parameters.</summary>
    public IReadOnlyList<SqlFunctionParameter> Parameters { get; }

    /// <summary>Gets the result type as declared.</summary>
    public string ReturnType { get; }

    /// <summary>Gets the volatility keyword; VOLATILE by default.</summary>
    public string Volatility { get; }

    /// <summary>Gets the whether a NULL argument bypasses the body.</summary>
    public bool IsStrict { get; }

    /// <summary>Gets the single expression body.</summary>
    public SqlExpression Body { get; }
}
