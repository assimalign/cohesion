namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The matched DELETE action.</summary>
public sealed record OqlMergeDeleteAction
{
    /// <summary>Initializes a DELETE action.</summary>
    /// <param name="predicate">The optional action WHERE expression.</param>
    public OqlMergeDeleteAction(OqlExpression? predicate = null)
    {
        Predicate = predicate;
    }

    /// <summary>Gets the optional action WHERE expression.</summary>
    public OqlExpression? Predicate { get; }
}
