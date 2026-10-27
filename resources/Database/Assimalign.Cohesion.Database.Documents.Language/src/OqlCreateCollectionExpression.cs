using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A <c>CREATE COLLECTION [IF NOT EXISTS]</c> statement.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlCreateCollectionExpression {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateCollectionExpression.DebugView))]
public sealed class OqlCreateCollectionExpression : OqlExpression
{
    /// <summary>Initializes a collection creation statement.</summary>
    /// <param name="name">The database-local collection name.</param>
    /// <param name="ifNotExists">Whether an existing collection makes the statement a no-op.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlCreateCollectionExpression(string name, bool ifNotExists = false, Location? location = null) : base(location)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    /// <summary>Gets the database-local collection name.</summary>
    public string Name { get; }

    /// <summary>Gets whether an existing collection makes the statement a no-op.</summary>
    public bool IfNotExists { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateCollectionExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateCollectionExpression value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public bool IfNotExists => _target.IfNotExists;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
