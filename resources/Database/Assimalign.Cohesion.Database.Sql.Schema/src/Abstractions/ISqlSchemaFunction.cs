using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a database function declaration.</summary>
public interface ISqlSchemaFunction
{
    /// <summary>Gets the function name.</summary>
    string Name { get; }

    /// <summary>Gets the expression body retained for schema compilation.</summary>
    LambdaExpression Body { get; }
}
