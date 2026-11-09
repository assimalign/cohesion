using System;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The unmatched INSERT action. Its target binding is MISSING.</summary>
public sealed record OqlMergeInsertAction
{
    /// <summary>Initializes an INSERT action.</summary>
    /// <param name="key">The ANSI insert key; null for lookup INSERT, which uses ON KEY.</param>
    /// <param name="value">The inserted document expression.</param>
    /// <param name="predicate">The optional action WHERE expression.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public OqlMergeInsertAction(OqlExpression? key, OqlExpression value, OqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        Key = key; Value = value; Predicate = predicate;
    }

    /// <summary>Gets the optional ANSI key.</summary>
    public OqlExpression? Key { get; }
    /// <summary>Gets the document value.</summary>
    public OqlExpression Value { get; }
    /// <summary>Gets the optional action filter.</summary>
    public OqlExpression? Predicate { get; }
}
