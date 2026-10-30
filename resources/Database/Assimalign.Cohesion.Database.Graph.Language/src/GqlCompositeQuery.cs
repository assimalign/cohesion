using Assimalign.Cohesion.Database.Graph.Language.Internal;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An explicitly grouped binary query composition.</summary>
/// <param name="Left">The left query, evaluated first.</param>
/// <param name="Operator">The result combination operation.</param>
/// <param name="Right">The right query, with its own binding scope.</param>
public sealed record GqlCompositeQuery(GqlQueryExpression Left, GqlCompositeOperator Operator, GqlQueryExpression Right)
{
    /// <summary>Renders the query tree with explicit grouping.</summary>
    /// <returns>GQL preserving the operand and operator structure.</returns>
    public override string ToString() => GqlPathRenderer.RenderComposite(this);
}
