using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    /// <summary>
    /// Parses an ISO/IEC 39075 <c>&lt;is label expression&gt;</c> after an element variable:
    /// <c>:</c> or <c>IS</c>, then a label expression. A node pattern also takes the Cohesion
    /// convenience <c>:A:B</c>, which means <c>:A&amp;B</c> and repeats plain names only.
    /// </summary>
    /// <param name="node">Whether the element is a node pattern.</param>
    /// <returns>The expression, or <see langword="null"/> when the element names no labels.</returns>
    private GqlLabelExpression? ParseLabelSpecification(bool node)
    {
        bool colon = Current.Type == TokenType.Colon;
        if (!colon && !(Current.Type == TokenType.Keyword && Is("IS"))) { return null; }
        Advance();
        int first = _position;
        var expression = ParseLabelDisjunction();
        if (Failed || Current.Type != TokenType.Colon) { return expression; }

        if (!node)
        {
            Error("GQL0002", "A relationship has one type: write one label expression, such as :T|U; ':A:B' repeats node labels.", Current);
            return expression;
        }
        if (!colon)
        {
            Error("GQL0002", "':' cannot follow an IS label expression; write IS A&B.", Current);
            return expression;
        }
        // The convenience starts from one plain name: '%' is also one token, so (n:%:A) needs
        // the type test as well as the token count.
        if (expression is not GqlLabelName || _position != first + 1)
        {
            Error("GQL0002", mixedColonMessage, Current);
            return expression;
        }

        // :A:B:C is one conjunction, as long as the list runs (Neo4j: ColonConjunction, then
        // Conjunctions.flat); no count of labels applies.
        List<GqlLabelExpression> operands = [expression];
        while (!Failed && Take(TokenType.Colon))
        {
            if (Current.Type is TokenType.Bang or TokenType.Percent or TokenType.LeftParen)
            {
                Error("GQL0002", mixedColonMessage, Current);
                break;
            }
            operands.Add(new GqlLabelName(Identifier(allowKeyword: true)));
        }
        if (!Failed && Current.Type is TokenType.Pipe or TokenType.Ampersand or TokenType.Concat)
        {
            Error("GQL0002", mixedColonMessage, Current);
        }
        return GqlLabelConjunction.FromOwnedList(operands);
    }

    private const string mixedColonMessage =
        "Repeated ':' labels cannot be combined with label-expression operators; write ':A&B' or ':A:B'.";

    // label-expression := label-term ('|' label-term)*
    private GqlLabelExpression ParseLabelDisjunction()
    {
        var first = ParseLabelConjunction();
        if (Failed || Current.Type is not (TokenType.Pipe or TokenType.Concat)) { return first; }

        // A parenthesized disjunction that opens the chain merges into it: (A|B)|C is A|B|C. A term
        // is a disjunction only when it came from parentheses.
        List<GqlLabelExpression> operands = first is GqlLabelDisjunction opening ? opening.DetachOperands() : [first];
        while (!Failed && Current.Type is TokenType.Pipe or TokenType.Concat)
        {
            if (Current.Type == TokenType.Concat)
            {
                Error("GQL0002", "'||' is not a label operator; write one '|' between labels.", Current);
                break;
            }
            Advance();
            operands.Add(ParseLabelConjunction());
        }
        return GqlLabelDisjunction.FromOwnedList(operands);
    }

    // label-term := label-factor ('&' label-factor)*
    private GqlLabelExpression ParseLabelConjunction()
    {
        var first = ParseLabelFactor();
        if (Failed || Current.Type != TokenType.Ampersand) { return first; }

        // A parenthesized conjunction that opens the chain merges into it: (A&B)&C is A&B&C. A
        // factor is a conjunction only when it came from parentheses.
        List<GqlLabelExpression> operands = first is GqlLabelConjunction opening ? opening.DetachOperands() : [first];
        while (!Failed && Take(TokenType.Ampersand))
        {
            operands.Add(ParseLabelFactor());
        }
        return GqlLabelConjunction.FromOwnedList(operands);
    }

    // label-factor := '!' label-primary | label-primary
    private GqlLabelExpression ParseLabelFactor()
    {
        if (!Take(TokenType.Bang)) { return ParseLabelPrimary(negated: false); }
        return new GqlLabelNegation(ParseLabelPrimary(negated: true));
    }

    // label-primary := label-name | '%' | '(' label-expression ')'
    private GqlLabelExpression ParseLabelPrimary(bool negated)
    {
        var token = Current;
        if (Take(TokenType.Percent)) { return new GqlLabelWildcard(); }
        if (token.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function or TokenType.QuotedIdentifier)
        {
            return new GqlLabelName(Identifier(allowKeyword: true));
        }
        if (token.Type == TokenType.LeftParen)
        {
            // The one place a label expression recurses: Neo4j bounds this nesting only by the
            // stack, and so does this parser, with a diagnostic instead of an overflow.
            if (!HasStackToNest(token)) { return new GqlLabelWildcard(); }
            Advance();
            var inner = ParseLabelDisjunction();
            Expect(TokenType.RightParen, "')'");
            return inner;
        }

        string message = token.Type == TokenType.Colon
            ? "A label expression follows one ':'; write ':A|B', not ':A|:B'."
            : negated
                ? "Expected a label name, '%' or '(' after '!'; '!' negates one label, '%' or parenthesized expression."
                : "Expected a label name, '%', '!' or '('.";
        Error("GQL0002", message, token);
        return new GqlLabelWildcard();
    }

    /// <summary>
    /// The names of a pure conjunction (<c>A</c>, <c>A&amp;B</c>, <c>:A:B</c>, <c>A&amp;(B&amp;C)</c>),
    /// left to right, which fill <see cref="GqlNodePattern.Labels"/>; empty when the expression is
    /// absent or uses <c>|</c>, <c>!</c> or <c>%</c>. Walks the tree with an explicit stack.
    /// </summary>
    private static IReadOnlyList<string> ConjunctionNames(GqlLabelExpression? expression)
    {
        if (expression is null) { return []; }
        List<string> names = [];
        var pending = new Stack<GqlLabelExpression>();
        pending.Push(expression);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case GqlLabelName name:
                    names.Add(name.Name);
                    break;
                case GqlLabelConjunction conjunction:
                    for (int i = conjunction.Operands.Count - 1; i >= 0; i--) { pending.Push(conjunction.Operands[i]); }
                    break;
                default:
                    return [];
            }
        }
        return names.AsReadOnly();
    }

    /// <summary>
    /// Whether a <c>WHERE</c> comparison is an ISO labeled predicate: an element variable followed
    /// by <c>:</c>, <c>IS LABELED</c> or <c>IS NOT LABELED</c>.
    /// </summary>
    private bool StartsLabeledPredicate()
    {
        if (Current.Type is not (TokenType.Identifier or TokenType.QuotedIdentifier)) { return false; }
        int next = _position + 1;
        if (next < _tokens.Count && _tokens[next].Type == TokenType.Colon) { return true; }
        if (next >= _tokens.Count || _tokens[next].Type != TokenType.Keyword || !IsWordAt(next, "IS")) { return false; }
        int labeled = IsWordAt(next + 1, "NOT") ? next + 2 : next + 1;
        return IsWordAt(labeled, GqlLabelVocabulary.Labeled);
    }

    // labeled-predicate := variable (':' | IS [NOT] LABELED) label-expression
    private GqlLabeledPredicate ParseLabeledPredicate()
    {
        var start = Current;
        string variable = Identifier();
        bool negated = false;
        if (!Take(TokenType.Colon))
        {
            Expect("IS");
            negated = Take("NOT");
            Expect(GqlLabelVocabulary.Labeled);
        }
        var labels = ParseLabelDisjunction();
        if (!Failed && Current.Type == TokenType.Colon)
        {
            Error("GQL0002", "Repeated ':' labels are a node-pattern convenience; write n:A&B in a predicate.", Current);
        }
        return new GqlLabeledPredicate(variable, labels, negated, Span(start, Previous));
    }
}
