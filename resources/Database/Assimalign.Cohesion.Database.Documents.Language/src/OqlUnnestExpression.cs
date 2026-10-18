using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An array expansion binding in a SELECT FROM clause.</summary>
public sealed class OqlUnnestExpression : OqlExpression
{
    /// <summary>Initializes an array binding.</summary>
    /// <param name="collection">The array expression evaluated in the preceding scope.</param>
    /// <param name="variable">The bound variable, including an inferred path name.</param>
    /// <param name="isLeft">Whether to preserve empty or non-array input with MISSING.</param>
    /// <param name="location">The source span.</param>
    public OqlUnnestExpression(OqlExpression collection, string variable, bool isLeft = false, Location? location = null) : base(location)
    {
        Collection = collection;
        Variable = variable;
        IsLeft = isLeft;
    }

    /// <summary>Gets the array expression.</summary>
    public OqlExpression Collection { get; }
    /// <summary>Gets the variable name.</summary>
    public string Variable { get; }
    /// <summary>Gets whether empty or non-array input is preserved.</summary>
    public bool IsLeft { get; }
}
