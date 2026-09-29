using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// This represents a base node for all query expressions in the syntax tree. It serves as a common ancestor for various types of query expressions, such as select statements, insert statements, etc. Each specific query expression will inherit from this base class and provide
/// </summary>
public abstract class QueryExpression
{
    protected QueryExpression() { }
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
    /// 
    /// </summary>
    public virtual Location? Location { get; }
}
