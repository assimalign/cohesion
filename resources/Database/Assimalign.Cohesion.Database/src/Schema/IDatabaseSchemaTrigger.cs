using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes a database trigger declaration.</summary>
public interface IDatabaseSchemaTrigger
{
    /// <summary>Gets the target table row type.</summary>
    Type RowType { get; }

    /// <summary>Gets the event that invokes the trigger.</summary>
    TriggerEvent Event { get; }

    /// <summary>Gets the expression body retained for schema compilation.</summary>
    LambdaExpression Body { get; }
}
