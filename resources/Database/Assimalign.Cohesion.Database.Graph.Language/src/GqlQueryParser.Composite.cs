using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    // NEXT separates complete composite expressions. Parenthesized operands recursively
    // parse this level, so a NEXT inside a group stays within that operand.
    private GqlQueryExpression ParseNextQuery(bool hasInput = false)
    {
        var start = Current;
        var left = ParseCompositeQuery(hasInput);
        if (Failed || !Is("NEXT")) { return left; }
        var stages = new List<GqlStage>();
        AppendQueryStages(left, stages);
        while (!Failed && Take("NEXT"))
        {
            var next = Previous;
            if (stages.Count == 0 || stages[^1] is GqlReturnStage { IsWith: true } ||
                stages[^1] is not (GqlReturnStage or GqlCompositeStage) &&
                !stages.Exists(stage => stage is GqlInsertStage or GqlMergeStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage) ||
                Current.Type is TokenType.Eof or TokenType.Semicolon or TokenType.RightParen || Is("NEXT"))
            { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid, next); break; }
            stages.Add(new GqlNextStage(Span(next, next)));
            AppendQueryStages(ParseCompositeQuery(hasInput: true), stages);
        }
        return new GqlQueryExpression(stages.AsReadOnly(), Span(start, Previous));
    }

    private static void AppendQueryStages(GqlQueryExpression query, List<GqlStage> stages)
    {
        if (query.Composite is { } composite) { stages.Add(new GqlCompositeStage(composite, query.Location)); }
        else { stages.AddRange(query.Stages); }
    }

    private GqlQueryExpression ParseCompositeQuery(bool hasInput = false)
    {
        var start = Current;
        var left = ParseQueryOperand(hasInput);
        // ISO/IEC 39075 14.2 has one left-recursive query-conjunction level.
        while (!Failed && (Is("UNION") || Is("EXCEPT") || Is("INTERSECT") || Is("OTHERWISE")))
        {
            var operation = Take("UNION") ? GqlCompositeOperator.Union :
                Take("EXCEPT") ? GqlCompositeOperator.Except :
                Take("INTERSECT") ? GqlCompositeOperator.Intersect : GqlCompositeOperator.Otherwise;
            if (operation == GqlCompositeOperator.Otherwise) { Take("OTHERWISE"); }
            else if (Take("ALL"))
            {
                operation = operation switch
                {
                    GqlCompositeOperator.Union => GqlCompositeOperator.UnionAll,
                    GqlCompositeOperator.Except => GqlCompositeOperator.ExceptAll,
                    _ => GqlCompositeOperator.IntersectAll,
                };
            }
            else { Take("DISTINCT"); }
            var right = ParseQueryOperand(hasInput);
            left = new GqlQueryExpression(new GqlCompositeQuery(left, operation, right), Span(start, Previous));
        }
        if (!Failed && (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT")) &&
            (left.Composite is not null || left.Stages.Any(stage => stage is GqlCompositeStage)))
        { Unsupported("COMPOSITE ORDER BY AND PAGE STATEMENT", Current); }
        return left;
    }

    private GqlQueryExpression ParseQueryOperand(bool hasInput)
    {
        var start = Current;
        if (Take(TokenType.LeftParen))
        {
            if (!HasStackToNest(start)) { return EmptyExpression(); }
            var query = ParseNextQuery(hasInput);
            Expect(TokenType.RightParen, "')'");
            return query;
        }
        if (!IsQueryStart() && !Is("NEXT") && !(hasInput && (Is("DELETE") || Is("DETACH") || Is("SET") || Is("REMOVE"))))
        {
            if (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT"))
            { Unsupported("STANDALONE ORDER BY AND PAGE STATEMENT", Current); }
            else { Error("CG00015", ErrorMessages.CompositeOperandRequired, Current); }
            return EmptyExpression();
        }
        var operand = ParseQuery(hasInput);
        if (!Failed) { operand.SetStatementText(_source[start.Start..Previous.End]); }
        return operand;
    }
}
