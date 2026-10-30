using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A binding evaluated in the scope of the preceding FROM sources.</summary>
// Deviates from the repo interface-first rule per design decision: Database source bindings are a closed AST variant family (database-area.md).
public abstract class OqlSourceBindingExpression : OqlExpression
{
    private protected OqlSourceBindingExpression(OqlExpression expression, string variable, bool isLeft, Location? location) : base(location)
    { Expression = expression; Variable = variable; IsLeft = isLeft; }

    /// <summary>Gets the array or document-key expression evaluated before introducing this variable.</summary>
    public OqlExpression Expression { get; }
    /// <summary>Gets the bound variable.</summary>
    public string Variable { get; }
    /// <summary>Gets whether an unmatched input is preserved.</summary>
    public bool IsLeft { get; }
}
