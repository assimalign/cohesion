namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Specifies how a scalar comparison combines subquery rows.</summary>
public enum SqlComparisonQuantifier
{
    /// <summary>Returns true when any comparison is true.</summary>
    Any,
    /// <summary>A spelling of <see cref="Any"/> with identical semantics.</summary>
    Some,
    /// <summary>Returns true when every comparison is true.</summary>
    All,
}
