using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An ordinal, case-sensitive string pattern predicate.</summary>
public sealed class OqlLikeExpression : OqlExpression
{
    /// <summary>Initializes a LIKE predicate.</summary>
    /// <param name="operand">The input string.</param>
    /// <param name="pattern">The pattern with percent and underscore wildcards.</param>
    /// <param name="escape">The escape expression; null selects backslash, an empty string disables escaping.</param>
    /// <param name="negated">Whether this is NOT LIKE.</param>
    /// <param name="location">The source span.</param>
    public OqlLikeExpression(OqlExpression operand, OqlExpression pattern, OqlExpression? escape = null, bool negated = false, Location? location = null) : base(location)
    {
        Operand = operand;
        Pattern = pattern;
        Escape = escape;
        Negated = negated;
    }

    /// <summary>Gets the input string.</summary>
    public OqlExpression Operand { get; }
    /// <summary>Gets the pattern.</summary>
    public OqlExpression Pattern { get; }
    /// <summary>Gets the escape expression, or null for the default backslash.</summary>
    public OqlExpression? Escape { get; }
    /// <summary>Gets whether the result is negated.</summary>
    public bool Negated { get; }
}
