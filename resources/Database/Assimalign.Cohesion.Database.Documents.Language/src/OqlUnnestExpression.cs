using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An array expansion binding in a SELECT FROM clause.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlUnnestExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression.DebugView))]
public sealed class OqlUnnestExpression : OqlSourceBindingExpression
{
    private readonly bool _hasExplicitAlias;

    /// <summary>Initializes an array binding.</summary>
    /// <param name="collection">The array expression evaluated in the preceding scope.</param>
    /// <param name="variable">The bound variable, including an inferred path name.</param>
    /// <param name="isLeft">Whether to preserve empty or non-array input with MISSING.</param>
    /// <param name="location">The source span.</param>
    /// <param name="isAliasImplicit">Whether the variable was inferred from the array path rather than explicitly declared.</param>
    public OqlUnnestExpression(OqlExpression collection, string variable, bool isLeft = false, Location? location = null,
        bool isAliasImplicit = false) : base(collection, variable, isLeft, location)
    {
        _hasExplicitAlias = !isAliasImplicit;
    }

    /// <summary>Gets the explicit alias, or null when Variable is the inferred path name.</summary>
    public string? Alias => _hasExplicitAlias ? Variable : null;
    /// <summary>Gets the array expression.</summary>
    public OqlExpression Collection => Expression;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Collection => _target.Collection;
        public string Variable => _target.Variable;
        public string? Alias => _target.Alias;
        public bool IsLeft => _target.IsLeft;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
