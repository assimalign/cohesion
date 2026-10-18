using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Describes a document query, INSERT, UPDATE or DELETE without executing it.</summary>
public sealed class OqlExplainExpression : OqlExpression
{
    internal OqlExplainExpression(OqlExpression statement, Location? location) : base(location) => Statement = statement;

    /// <summary>Gets the query, INSERT, UPDATE or DELETE statement to plan.</summary>
    public OqlExpression Statement { get; }
}
