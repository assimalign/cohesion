using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Describes a document query, INSERT, UPSERT, UPDATE or DELETE without executing it.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlExplainExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlExplainExpression.DebugView))]
public sealed class OqlExplainExpression : OqlExpression
{
    internal OqlExplainExpression(OqlExpression statement, Location? location) : base(location) => Statement = statement;

    /// <summary>Gets the query, INSERT, UPSERT, UPDATE or DELETE statement to plan.</summary>
    public OqlExpression Statement { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlExplainExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlExplainExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Statement => _target.Statement;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
