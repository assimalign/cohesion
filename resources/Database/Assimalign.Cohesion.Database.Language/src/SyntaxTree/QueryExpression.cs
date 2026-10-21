using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Provides the base node for model-specific query expressions, with optional source text and location.
/// </summary>
public abstract class QueryExpression
{
    /// <summary>Initializes an expression without source text or location.</summary>
    protected QueryExpression() { }
    /// <summary>Initializes an expression with its source text and location.</summary>
    /// <param name="text">The original expression text, or null when unavailable.</param>
    /// <param name="location">The expression's location in the original source.</param>
    protected QueryExpression(string? text, Location location)
    {
        Text = text;
        Location = location;
    }

    /// <summary>
    /// The raw text of the query expression, if available.
    /// </summary>
    public virtual string? Text { get; }

    /// <summary>
    /// Gets the source location, or null when the expression has no source location.
    /// </summary>
    public virtual Location? Location { get; }
}
