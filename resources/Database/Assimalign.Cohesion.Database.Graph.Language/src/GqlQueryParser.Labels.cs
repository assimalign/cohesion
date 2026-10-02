using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    // Bounds both the label tree's depth and its parenthesis nesting, as GQL0005 bounds predicates.
    private const int maximumLabelDepth = 128;

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
        _labelNesting = 0;
        int first = _position;
        var (expression, depth) = ParseLabelDisjunction();
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
        if (_position != first + 1)
        {
            Error("GQL0002", mixedColonMessage, Current);
            return expression;
        }

        while (!Failed && Take(TokenType.Colon))
        {
            if (Current.Type is TokenType.Bang or TokenType.Percent or TokenType.LeftParen)
            {
                Error("GQL0002", mixedColonMessage, Current);
                break;
            }
            var name = Current;
            expression = new GqlLabelConjunction(expression, new GqlLabelName(Identifier(allowKeyword: true)));
            depth = Deepen(depth, 1, name);
        }
        if (!Failed && Current.Type is TokenType.Pipe or TokenType.Ampersand or TokenType.Concat)
        {
            Error("GQL0002", mixedColonMessage, Current);
        }
        return expression;
    }

    private const string mixedColonMessage =
        "Repeated ':' labels cannot be combined with label-expression operators; write ':A&B' or ':A:B'.";

    // label-expression := label-term ('|' label-term)*
    private (GqlLabelExpression Expression, int Depth) ParseLabelDisjunction()
    {
        var (left, depth) = ParseLabelConjunction();
        while (!Failed && Current.Type is TokenType.Pipe or TokenType.Concat)
        {
            var operation = Current;
            if (operation.Type == TokenType.Concat)
            {
                Error("GQL0002", "'||' is not a label operator; write one '|' between labels.", operation);
                break;
            }
            Advance();
            var (right, rightDepth) = ParseLabelConjunction();
            left = new GqlLabelDisjunction(left, right);
            depth = Deepen(depth, rightDepth, operation);
        }
        return (left, depth);
    }

    // label-term := label-factor ('&' label-factor)*
    private (GqlLabelExpression Expression, int Depth) ParseLabelConjunction()
    {
        var (left, depth) = ParseLabelFactor();
        while (!Failed && Current.Type == TokenType.Ampersand)
        {
            var operation = Current;
            Advance();
            var (right, rightDepth) = ParseLabelFactor();
            left = new GqlLabelConjunction(left, right);
            depth = Deepen(depth, rightDepth, operation);
        }
        return (left, depth);
    }

    // label-factor := '!' label-primary | label-primary
    private (GqlLabelExpression Expression, int Depth) ParseLabelFactor()
    {
        if (Current.Type != TokenType.Bang) { return ParseLabelPrimary(negated: false); }
        var operation = Current;
        Advance();
        var (operand, depth) = ParseLabelPrimary(negated: true);
        return (new GqlLabelNegation(operand), Deepen(depth, 0, operation));
    }

    // label-primary := label-name | '%' | '(' label-expression ')'
    private (GqlLabelExpression Expression, int Depth) ParseLabelPrimary(bool negated)
    {
        var token = Current;
        if (Take(TokenType.Percent)) { return (new GqlLabelWildcard(), 1); }
        if (token.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function or TokenType.QuotedIdentifier)
        {
            return (new GqlLabelName(Identifier(allowKeyword: true)), 1);
        }
        if (token.Type == TokenType.LeftParen)
        {
            if (++_labelNesting > maximumLabelDepth)
            {
                Error("GQL0005", "Label-expression nesting cannot exceed 128 levels.", token);
                return (new GqlLabelWildcard(), 1);
            }
            Advance();
            var inner = ParseLabelDisjunction();
            Expect(TokenType.RightParen, "')'");
            _labelNesting--;
            return inner;
        }

        string message = token.Type == TokenType.Colon
            ? "A label expression follows one ':'; write ':A|B', not ':A|:B'."
            : negated
                ? "Expected a label name, '%' or '(' after '!'; '!' negates one label, '%' or parenthesized expression."
                : "Expected a label name, '%', '!' or '('.";
        Error("GQL0002", message, token);
        return (new GqlLabelWildcard(), 1);
    }

    /// <summary>The depth of a node over operands of the given depths; past 128 it is <c>GQL0005</c>.</summary>
    private int Deepen(int left, int right, Lexeme operation)
    {
        int depth = (left > right ? left : right) + 1;
        if (depth > maximumLabelDepth && !Failed)
        {
            Error("GQL0005", "Label-expression nesting cannot exceed 128 levels.", operation);
        }
        return depth;
    }

    /// <summary>
    /// The names of a pure conjunction (<c>A</c>, <c>A&amp;B</c>, <c>:A:B</c>), left to right, which
    /// fill <see cref="GqlNodePattern.Labels"/>; empty when the expression is absent or uses
    /// <c>|</c>, <c>!</c> or <c>%</c>.
    /// </summary>
    private static IReadOnlyList<string> ConjunctionNames(GqlLabelExpression? expression)
    {
        List<string> names = [];
        return expression is not null && CollectConjunction(expression, names) ? names.AsReadOnly() : [];

        static bool CollectConjunction(GqlLabelExpression expression, List<string> names)
        {
            switch (expression)
            {
                case GqlLabelName name:
                    names.Add(name.Name);
                    return true;
                case GqlLabelConjunction conjunction:
                    return CollectConjunction(conjunction.Left, names) && CollectConjunction(conjunction.Right, names);
                default:
                    return false;
            }
        }
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
        _labelNesting = 0;
        var (labels, _) = ParseLabelDisjunction();
        if (!Failed && Current.Type == TokenType.Colon)
        {
            Error("GQL0002", "Repeated ':' labels are a node-pattern convenience; write n:A&B in a predicate.", Current);
        }
        return new GqlLabeledPredicate(variable, labels, negated, Span(start, Previous));
    }
}
