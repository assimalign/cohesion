using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseCompositeQuery()
    {
        var start = Current;
        var left = ParseQueryOperand();
        while (!Failed && Take("UNION"))
        {
            var operation = Take("ALL") ? GqlCompositeOperator.UnionAll : GqlCompositeOperator.Union;
            if (Is("DISTINCT"))
            { Unsupported("UNION DISTINCT", Current); break; }
            var right = ParseQueryOperand();
            left = new GqlQueryExpression(new GqlCompositeQuery(left, operation, right), Span(start, Previous));
        }
        if (!Failed && (Is("EXCEPT") || Is("INTERSECT") || Is("OTHERWISE")))
        { Unsupported(Current.Text.ToUpperInvariant(), Current); }
        if (!Failed && left.Composite is not null && (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT")))
        { Unsupported("COMPOSITE ORDER BY AND PAGE STATEMENT", Current); }
        return left;
    }

    private GqlQueryExpression ParseQueryOperand()
    {
        var start = Current;
        if (Take(TokenType.LeftParen))
        {
            if (!HasStackToNest(start)) { return EmptyExpression(); }
            var query = ParseCompositeQuery();
            Expect(TokenType.RightParen, "')'");
            return query;
        }
        if (!IsQueryStart() && !Is("NEXT"))
        {
            Error("CG00015", ErrorMessages.CompositeOperandRequired, Current);
            return EmptyExpression();
        }
        var operand = ParseQuery();
        operand.SetStatementText(_source[start.Start..Previous.End]);
        return operand;
    }
}
