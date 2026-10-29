using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An array expansion binding in a SELECT FROM clause.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlUnnestExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression.DebugView))]
public sealed class OqlUnnestExpression : OqlSourceBindingExpression
{
    /// <summary>Initializes an array binding.</summary>
    /// <param name="collection">The array expression evaluated in the preceding scope.</param>
    /// <param name="variable">The bound variable, including an inferred path name.</param>
    /// <param name="isLeft">Whether to preserve empty or non-array input with MISSING.</param>
    /// <param name="location">The source span.</param>
    public OqlUnnestExpression(OqlExpression collection, string variable, bool isLeft = false, Location? location = null) : base(collection, variable, isLeft, location)
    {
    }

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
        public bool IsLeft => _target.IsLeft;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
