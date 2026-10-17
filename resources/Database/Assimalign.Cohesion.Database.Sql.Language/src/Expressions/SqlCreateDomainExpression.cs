using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a parsed domain CREATE statement.</summary>
public sealed class SqlCreateDomainExpression : SqlQueryExpression
{
    internal SqlCreateDomainExpression(string name, string baseType, SqlExpression? defaultValue, bool isNotNull, string? constraintName, SqlExpression? check, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    {
        Name = name;
        BaseType = baseType;
        DefaultValue = defaultValue;
        IsNotNull = isNotNull;
        ConstraintName = constraintName;
        Check = check;
    }

    /// <summary>Gets the domain name.</summary>
    public string Name { get; }

    /// <summary>Gets the base type as declared.</summary>
    public string BaseType { get; }

    /// <summary>Gets the optional default literal.</summary>
    public SqlExpression? DefaultValue { get; }

    /// <summary>Gets the whether NULL is refused.</summary>
    public bool IsNotNull { get; }

    /// <summary>Gets the optional CHECK name.</summary>
    public string? ConstraintName { get; }

    /// <summary>Gets the optional predicate over VALUE.</summary>
    public SqlExpression? Check { get; }
}
