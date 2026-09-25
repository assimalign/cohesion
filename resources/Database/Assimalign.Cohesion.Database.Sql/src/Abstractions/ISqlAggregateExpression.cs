using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Describes a symbolic aggregate in a C# database function declaration.
/// </summary>
public interface ISqlAggregateExpression
{
    /// <summary>Gets the source table row type.</summary>
    Type SourceType { get; }

    /// <summary>Gets the aggregate value selector.</summary>
    LambdaExpression Selector { get; }

    /// <summary>Gets the aggregate row predicate.</summary>
    LambdaExpression Predicate { get; }
}
