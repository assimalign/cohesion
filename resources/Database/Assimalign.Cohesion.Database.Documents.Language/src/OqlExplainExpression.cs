using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Describes a document query without executing it.</summary>
public sealed class OqlExplainExpression : OqlExpression
{
    internal OqlExplainExpression(OqlSelectExpression statement, Location? location) : base(location) => Statement = statement;

    /// <summary>Gets the SELECT query to plan.</summary>
    public OqlSelectExpression Statement { get; }
}
