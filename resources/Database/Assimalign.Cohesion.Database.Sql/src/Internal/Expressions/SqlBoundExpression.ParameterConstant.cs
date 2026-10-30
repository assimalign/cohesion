namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A folded value that retains parameter provenance and cannot witness predicate implication.</summary>
internal sealed class SqlBoundParameterConstant : SqlBoundConstant
{
    internal SqlBoundParameterConstant(object? value)
        : base(value)
    {
    }
}
