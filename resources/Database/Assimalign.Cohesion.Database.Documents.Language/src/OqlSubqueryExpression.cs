using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A SELECT expression whose result is an array of zero or more rows.</summary>
[System.Diagnostics.DebuggerDisplay("OqlSubqueryExpression")]
[System.Diagnostics.DebuggerTypeProxy(typeof(OqlSubqueryExpression.DebugView))]
public sealed class OqlSubqueryExpression : OqlExpression
{
    /// <summary>Initializes an expression subquery.</summary>
    /// <param name="query">The SELECT evaluated in a nested query scope.</param>
    /// <param name="location">The complete parenthesized source span.</param>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public OqlSubqueryExpression(OqlSelectExpression query, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
    }

    /// <summary>Gets the nested SELECT, including its projection mode.</summary>
    public OqlSelectExpression Query { get; }
    private sealed class DebugView
    {
        private readonly OqlSubqueryExpression _target;
        public DebugView(OqlSubqueryExpression value) { _target = value; }
        public OqlSelectExpression Query => _target.Query;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
