using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Deletes documents matching an optional predicate from one collection.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlDeleteExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlDeleteExpression.DebugView))]
public sealed class OqlDeleteExpression : OqlExpression
{
    /// <summary>Initializes a DELETE statement.</summary>
    /// <param name="collection">The target collection.</param>
    /// <param name="alias">The optional source alias.</param>
    /// <param name="predicate">The filter, or null to delete every document.</param>
    /// <param name="location">The source span.</param>
    public OqlDeleteExpression(string collection, string? alias, OqlExpression? predicate, Location? location = null)
        : base(location)
    {
        Collection = collection;
        Alias = alias;
        Predicate = predicate;
    }

    /// <summary>Gets the target collection.</summary>
    public string Collection { get; }
    /// <summary>Gets the optional source alias.</summary>
    public string? Alias { get; }
    /// <summary>Gets the filter, or null to delete every document.</summary>
    public OqlExpression? Predicate { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlDeleteExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlDeleteExpression value)
        {
            _target = value;
        }

        public string Collection => _target.Collection;
        public string? Alias => _target.Alias;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Predicate => _target.Predicate;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
