namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A function metadata query-value category.</summary>
public enum GraphFunctionValueKind
{
    /// <summary>Any supported query value.</summary>
    Any,
    /// <summary>Any numeric scalar.</summary>
    Number,
    /// <summary>Any supported scalar.</summary>
    Scalar,
    /// <summary>A node.</summary>
    Node,
    /// <summary>A relationship.</summary>
    Relationship,
    /// <summary>A path.</summary>
    Path,
    /// <summary>A list.</summary>
    List,
    /// <summary>A string-keyed map.</summary>
    Map,
    /// <summary>A string.</summary>
    String,
    /// <summary>A Boolean.</summary>
    Boolean,
    /// <summary>A date.</summary>
    Date,
    /// <summary>A timestamp.</summary>
    Timestamp,
    /// <summary>An integral number.</summary>
    Integral,
    /// <summary>A floating-point or decimal number.</summary>
    NonIntegral,
}
