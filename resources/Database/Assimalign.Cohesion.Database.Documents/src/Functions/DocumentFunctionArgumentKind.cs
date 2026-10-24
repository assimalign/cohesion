using System;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A nonempty mask of valued JSON kinds admitted by one function parameter.</summary>
/// <remarks>NULL and MISSING are controlled separately by DocumentNullBehavior. Signature masks may combine kinds; None and unknown bits are refused.</remarks>
[Flags]
public enum DocumentFunctionArgumentKind : byte
{
    /// <summary>No valued kind; invalid in a function signature.</summary>
    None = 0,
    /// <summary>TRUE or FALSE.</summary>
    Boolean = 1,
    /// <summary>An exact document number.</summary>
    Number = 2,
    /// <summary>A string.</summary>
    String = 4,
    /// <summary>A JSON array.</summary>
    Array = 8,
    /// <summary>A JSON object.</summary>
    Object = 16,
    /// <summary>A JSON array or object.</summary>
    Json = Array | Object,
    /// <summary>Any valued document kind.</summary>
    Any = Boolean | Number | String | Array | Object,
}
