using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A <c>DROP COLLECTION [IF EXISTS]</c> statement.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlDropCollectionExpression {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlDropCollectionExpression.DebugView))]
public sealed class OqlDropCollectionExpression : OqlExpression
{
    /// <summary>Initializes a collection removal statement.</summary>
    /// <param name="name">The database-local collection name.</param>
    /// <param name="ifExists">Whether an absent collection makes the statement a no-op.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlDropCollectionExpression(string name, bool ifExists = false, Location? location = null) : base(location)
    {
        Name = name;
        IfExists = ifExists;
    }

    /// <summary>Gets the database-local collection name.</summary>
    public string Name { get; }

    /// <summary>Gets whether an absent collection makes the statement a no-op.</summary>
    public bool IfExists { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlDropCollectionExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlDropCollectionExpression value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public bool IfExists => _target.IfExists;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
