using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Assigns a pre-update expression value to a document path.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlUpdateAssignment")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlUpdateAssignment.DebugView))]
public sealed class OqlUpdateAssignment : OqlExpression
{
    /// <summary>Initializes a SET assignment.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="value">The value read from the pre-update document.</param>
    /// <param name="location">The complete assignment span.</param>
    public OqlUpdateAssignment(OqlPathExpression path, OqlExpression value, Location? location = null) : base(location)
    {
        Path = path;
        Value = value;
    }

    /// <summary>Gets the target path.</summary>
    public OqlPathExpression Path { get; }
    /// <summary>Gets the expression evaluated against the pre-update document.</summary>
    public OqlExpression Value { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlUpdateAssignment _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlUpdateAssignment value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlPathExpression Path => _target.Path;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Value => _target.Value;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
