namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Whether a <see cref="SqlFunction"/> computes one value per row or one value per group of rows.</summary>
public enum SqlFunctionKind : byte
{
    /// <summary>A scalar function (<see cref="SqlScalarFunction"/>): one value from one row's arguments.</summary>
    Scalar,

    /// <summary>An aggregate function (<see cref="SqlAggregateFunction"/>): one value from the rows of a group.</summary>
    Aggregate,
}
