using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A Couchbase lookup or key-equality ANSI MERGE statement.</summary>
/// <remarks>Actions occur in UPDATE, DELETE, INSERT order. Execution is deferred to OQ-M2; EXPLAIN is available.</remarks>
[System.Diagnostics.DebuggerDisplay("OqlMergeExpression")]
[System.Diagnostics.DebuggerTypeProxy(typeof(OqlMergeExpression.DebugView))]
public sealed class OqlMergeExpression : OqlExpression
{
    /// <summary>Initializes a MERGE statement.</summary>
    /// <param name="collection">The target collection.</param>
    /// <param name="alias">The target alias; null uses the collection's implicit name.</param>
    /// <param name="source">The source collection, subquery or expression.</param>
    /// <param name="on">The lookup key or ANSI equality predicate.</param>
    /// <param name="isLookup">Whether ON KEY syntax is used.</param>
    /// <param name="isPrimary">Whether the lookup spells ON PRIMARY KEY.</param>
    /// <param name="update">The optional matched UPDATE action.</param>
    /// <param name="delete">The optional matched DELETE action.</param>
    /// <param name="insert">The optional unmatched INSERT action.</param>
    /// <param name="returning">The optional affected-image projection.</param>
    /// <param name="location">The source span.</param>
    public OqlMergeExpression(string collection, string? alias, OqlMergeSource source, OqlExpression on,
        bool isLookup, bool isPrimary = false, OqlMergeUpdateAction? update = null, OqlMergeDeleteAction? delete = null,
        OqlMergeInsertAction? insert = null, OqlReturningExpression? returning = null, Location? location = null) : base(location)
    {
        Collection = collection; Alias = alias; Source = source; On = on; IsLookup = isLookup; IsPrimary = isPrimary;
        Update = update; Delete = delete; Insert = insert; Returning = returning;
    }

    /// <summary>Gets the target collection.</summary>
    public string Collection { get; }
    /// <summary>Gets the explicit target alias, or null for its implicit name.</summary>
    public string? Alias { get; }
    /// <summary>Gets the source.</summary>
    public OqlMergeSource Source { get; }
    /// <summary>Gets the lookup key or ANSI predicate.</summary>
    public OqlExpression On { get; }
    /// <summary>Gets whether this is ON KEY syntax.</summary>
    public bool IsLookup { get; }
    /// <summary>Gets whether PRIMARY is written before KEY.</summary>
    public bool IsPrimary { get; }
    /// <summary>Gets the optional matched UPDATE action.</summary>
    public OqlMergeUpdateAction? Update { get; }
    /// <summary>Gets the optional matched DELETE action.</summary>
    public OqlMergeDeleteAction? Delete { get; }
    /// <summary>Gets the optional unmatched INSERT action.</summary>
    public OqlMergeInsertAction? Insert { get; }
    /// <summary>Gets the optional affected-image projection.</summary>
    public OqlReturningExpression? Returning { get; }

    /// <summary>Renders canonical OQL, preserving aliases, action order and expression structure.</summary>
    /// <returns>The canonical statement text.</returns>
    /// <exception cref="System.InvalidOperationException">An expression exceeds the rendering complexity or execution-stack limit, or has an unsupported AST shape.</exception>
    public override string ToString() => OqlMergeRenderer.Render(this);

    private sealed class DebugView
    {
        private readonly OqlMergeExpression _target;
        public DebugView(OqlMergeExpression target) { _target = target; }
        public string Collection => _target.Collection;
        public string? Alias => _target.Alias;
        public OqlMergeSource Source => _target.Source;
        public OqlExpression On => _target.On;
        public bool IsLookup => _target.IsLookup;
        public bool IsPrimary => _target.IsPrimary;
        public OqlMergeUpdateAction? Update => _target.Update;
        public OqlMergeDeleteAction? Delete => _target.Delete;
        public OqlMergeInsertAction? Insert => _target.Insert;
        public OqlReturningExpression? Returning => _target.Returning;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
