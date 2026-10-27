using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private static readonly IReadOnlyDictionary<string, GqlExpression> _noProperties =
        new ReadOnlyDictionary<string, GqlExpression>(new Dictionary<string, GqlExpression>(StringComparer.Ordinal));

    private IReadOnlyList<GqlPathPattern> ParsePatterns(bool allowPathVariable = false)
    {
        List<GqlPathPattern> paths = [];
        do
        {
            string? variable = null;
            if (allowPathVariable && IsVariableToken && _tokens[_position + 1].Type == TokenType.Equals)
            {
                variable = Identifier();
                Expect(TokenType.Equals, "'='");
            }
            GqlPathMode? mode = null;
            var prefix = Current;
            var prefixEnd = prefix;
            if (allowPathVariable && TryPathModeAt(_position, out var explicitMode))
            {
                mode = explicitMode;
                Advance();
                if (Is("PATH") || Is("PATHS")) { prefixEnd = Current; Advance(); }
            }
            var path = ParsePath(allowQuantifiers: allowPathVariable, out _);
            if (!Failed && mode == GqlPathMode.Walk && ContainsUnboundedQuantifier(path))
            { UnsupportedConstruct("UNBOUNDED WALK", prefix, prefixEnd); }
            paths.Add(path with { Variable = variable, Mode = mode });
        } while (!Failed && Take(TokenType.Comma));
        return paths.AsReadOnly();
    }

    private GqlPathPattern ParsePath(bool allowQuantifiers, out long bound)
    {
        bound = 0;
        var elements = new List<GqlPathElement>();
        bool needsNode = true;
        do
        {
            if (Current.Type == TokenType.LeftParen &&
                (_tokens[_position + 1].Type == TokenType.LeftParen ||
                 IsPathModeAt(_position + 1) ||
                 IsVariableLexeme(_tokens[_position + 1]) && _tokens[_position + 2].Type == TokenType.Equals))
            {
                var start = Current;
                if (!allowQuantifiers)
                { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid, start); break; }
                if (!HasStackToNest(start)) { break; }
                Advance();
                if (IsPathModeAt(_position))
                { RejectPathMode(); break; }
                if (IsVariableToken)
                { Unsupported("SUBPATH VARIABLE", Current); break; }
                var inner = ParsePath(allowQuantifiers, out long innerBound);
                if (!Failed && Is("WHERE")) { Unsupported("PARENTHESIZED PATH WHERE", Current); break; }
                Expect(TokenType.RightParen, "')'");
                RejectLineCommentAfterElement();
                var quantifierStart = Current;
                var quantifier = ParseQuantifier(cypher: false, allowQuantifiers);
                if (!Failed && quantifier is not null && ContainsQuantifier(inner))
                { Unsupported("NESTED QUANTIFIER", quantifierStart); }
                if (!Failed && quantifier is not null && innerBound == 0)
                { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid2, start); }
                elements.Add(new GqlParenthesizedPathPattern(inner, quantifier));
                bound += innerBound * (quantifier?.Max ?? quantifier?.Min ?? 1);
                needsNode = false;
            }
            else if (Current.Type == TokenType.LeftParen)
            {
                if (!needsNode && elements[^1] is GqlPathNode)
                { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid3, Current); break; }
                elements.Add(new GqlPathNode(ParseNode()));
                needsNode = false;
                if (!Failed && IsQuantifier)
                { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid4, Current); }
            }
            else if (!needsNode && Current.Type is TokenType.Minus or TokenType.LeftArrow or TokenType.RightArrow)
            {
                var edge = ParseRelationship();
                var start = Current;
                var quantifier = ParseQuantifier(cypher: false, allowQuantifiers);
                if (!Failed && edge.Quantifier is not null && quantifier is not null)
                { Unsupported("NESTED QUANTIFIER", start); }
                if (!allowQuantifiers && edge.Quantifier is not null && !Failed)
                { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid5, start); }
                elements.Add(new GqlPathRelationship(edge with { Quantifier = quantifier ?? edge.Quantifier }));
                var bounds = quantifier ?? edge.Quantifier;
                bound += bounds?.Max ?? bounds?.Min ?? 1;
                needsNode = true;
            }
            else if (!needsNode && TryFindUndirectedEdge(_position, out int last))
            { UnsupportedConstruct(GqlUnsupportedVocabulary.UndirectedEdge, Current, _tokens[last]); break; }
            else { break; }
            if (!Failed && bound > 64)
            { Error("CG00005", ErrorMessages.GqlQueryParserParsePathVoid6, Previous); }
        } while (!Failed);
        if (!Failed && (elements.Count == 0 || needsNode)) { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid7, Current); }
        bool parenthesized = elements.Any(element => element is GqlParenthesizedPathPattern);
        return new GqlPathPattern(parenthesized ? [] : elements.OfType<GqlPathNode>().Select(element => element.Pattern).ToArray(),
            parenthesized ? [] : elements.OfType<GqlPathRelationship>().Select(element => element.Pattern).ToArray())
        { Elements = elements.AsReadOnly() };
    }

    private bool IsPathModeAt(int index)
    {
        if (index >= _tokens.Count) { return false; }
        var end = _tokens[index];
        if (TryPathModeAt(index, out _))
        { return StartsPathPattern(index + 1, ref end); }
        return end.Text.ToUpperInvariant() is "ALL" or "ANY" or "SHORTEST" &&
            TryPathSearchPrefix(index, out _, out int last) && last + 1 < _tokens.Count && _tokens[last + 1].Type == TokenType.LeftParen;
    }

    private bool TryPathModeAt(int index, out GqlPathMode mode)
    {
        if (IsWordAt(index, "WALK")) { mode = GqlPathMode.Walk; return true; }
        if (IsWordAt(index, "TRAIL")) { mode = GqlPathMode.Trail; return true; }
        if (IsWordAt(index, "SIMPLE")) { mode = GqlPathMode.Simple; return true; }
        if (IsWordAt(index, "ACYCLIC")) { mode = GqlPathMode.Acyclic; return true; }
        mode = default;
        return false;
    }

    private void RejectPathMode()
    {
        if (Current.Text.ToUpperInvariant() is "ALL" or "ANY" or "SHORTEST" &&
            TryPathSearchPrefix(_position, out string construct, out int last))
        { UnsupportedConstruct(construct, Current, _tokens[last]); }
        else { Unsupported(Current.Text.ToUpperInvariant(), Current); }
    }

    private bool IsQuantifier => Current.Type is TokenType.Plus or TokenType.Asterisk ||
        Current.Type == TokenType.LeftBrace && _tokens[_position + 1].Type is TokenType.Integer or TokenType.Comma || Current.Text == "?";

    private GqlQuantifier? ParseQuantifier(bool cypher, bool allowQuantifiers)
    {
        if (Failed || !IsQuantifier) { return null; }
        var start = Current;
        if (Current.Text == "?")
        {
            _questionedPathPositions.Add(Current.Start);
            Unsupported("QUESTIONED PATH", Current);
            return null;
        }
        if (!allowQuantifiers)
        { Error("CG00002", ErrorMessages.GqlQueryParserParsePathVoid5, start); return null; }
        int min;
        int? max;
        if (cypher)
        {
            Expect(TokenType.Asterisk, "'*'");
            RejectLineCommentAfterElement();
            min = Current.Type == TokenType.Integer ? ParseQuantifierBound() : 1;
            max = min;
            if (Take(TokenType.DotDot)) { max = Current.Type == TokenType.Integer ? ParseQuantifierBound() : null; }
            else if (Previous.Type == TokenType.Asterisk) { max = null; }
        }
        else if (Take(TokenType.Plus)) { min = 1; max = null; }
        else if (Take(TokenType.Asterisk)) { min = 0; max = null; }
        else
        {
            Expect(TokenType.LeftBrace, "'{'");
            min = Current.Type == TokenType.Integer ? ParseQuantifierBound() : 0;
            max = min;
            if (Take(TokenType.Comma)) { max = Current.Type == TokenType.Integer ? ParseQuantifierBound() : null; }
            Expect(TokenType.RightBrace, "'}'");
        }
        RejectLineCommentAfterElement();
        if (!Failed && (max < min || !cypher && max == 0))
        { Error("CG00002", ErrorMessages.GqlQueryParserParseQuantifierVoid, start); }
        if (!Failed && IsQuantifier)
        {
            if (Current.Text == "?") { _questionedPathPositions.Add(Current.Start); }
            Unsupported("NESTED QUANTIFIER", Current);
        }
        return new GqlQuantifier(min, max);
    }

    private int ParseQuantifierBound()
    {
        var token = Current;
        Advance();
        if (int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int bound)) { return bound; }
        Error("CG00004", ErrorMessages.QueryParserParseQuantifierBoundVoid, token);
        return 0;
    }

    private static bool ContainsQuantifier(GqlPathPattern path)
    {
        var pending = new Stack<GqlPathPattern>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            if (current.Relationships.Any(edge => edge.Quantifier is not null)) { return true; }
            foreach (var element in current.Elements ?? [])
            {
                if (element is GqlPathRelationship { Pattern.Quantifier: not null } || element is GqlParenthesizedPathPattern { Quantifier: not null }) { return true; }
                if (element is GqlParenthesizedPathPattern group) { pending.Push(group.Pattern); }
            }
        }
        return false;
    }

    private static bool ContainsUnboundedQuantifier(GqlPathPattern path)
    {
        var pending = new Stack<GqlPathPattern>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            if (current.Relationships.Any(edge => edge.Quantifier is { Max: null })) { return true; }
            foreach (var element in current.Elements ?? [])
            {
                if (element is GqlPathRelationship { Pattern.Quantifier: { Max: null } } ||
                    element is GqlParenthesizedPathPattern { Quantifier: { Max: null } }) { return true; }
                if (element is GqlParenthesizedPathPattern group) { pending.Push(group.Pattern); }
            }
        }
        return false;
    }

    private GqlNodePattern ParseNode()
    {
        Expect(TokenType.LeftParen, "'('");
        // After a failure nothing more is read, so one malformed token reports one diagnostic.
        if (Failed) { return new GqlNodePattern(null, [], _noProperties); }
        string? variable = IsVariableToken ? Identifier() : null;
        var labels = ParseLabelSpecification(node: true);
        if (!Failed && (variable is not null || labels is not null) && Current.Type == TokenType.Parameter) { RejectUnsupportedParameter("WHOLE-MAP PARAMETER"); }
        var properties = ParseProperties();
        Expect(TokenType.RightParen, "')'");
        RejectLineCommentAfterElement();
        return new GqlNodePattern(variable, ConjunctionNames(labels), properties) { LabelExpression = labels };
    }

    /// <summary>
    /// Parses one ISO/IEC 39075 edge pattern (16.7). Full forms: <c>-[]-&gt;</c>,
    /// <c>&lt;-[]-</c>, <c>-[]-</c> and <c>&lt;-[]-&gt;</c>. Abbreviated forms: <c>-&gt;</c>,
    /// <c>&lt;-</c>, <c>-</c> and <c>&lt;-&gt;</c>, which the lexer reads as <c>&lt;-</c> then
    /// <c>&gt;</c> and the parser joins only when the two touch. The tilde forms never reach
    /// here: the capability scan rejects them.
    /// </summary>
    private GqlRelationshipPattern ParseRelationship()
    {
        if (Take(TokenType.RightArrow)) { return AbbreviatedEdge(GqlPatternDirection.Outgoing); }
        var arrow = Current;
        bool pointsLeft = arrow.Type == TokenType.LeftArrow;
        Advance(); // '<-' or '-'
        if (Current.Type != TokenType.LeftBracket)
        {
            if (pointsLeft && Current.Type == TokenType.GreaterThan && Current.Start == arrow.End)
            {
                Advance();
                return AbbreviatedEdge(GqlPatternDirection.LeftOrRight);
            }
            return AbbreviatedEdge(pointsLeft ? GqlPatternDirection.Incoming : GqlPatternDirection.Undirected);
        }

        Advance(); // '['
        string? variable = IsVariableToken ? Identifier() : null;
        var labels = ParseLabelSpecification(node: false);
        var quantifier = Current.Type == TokenType.Asterisk ? ParseQuantifier(cypher: true, allowQuantifiers: true) : null;
        if (!Failed && (variable is not null || labels is not null) && Current.Type == TokenType.Parameter) { RejectUnsupportedParameter("WHOLE-MAP PARAMETER"); }
        var properties = ParseProperties();
        Expect(TokenType.RightBracket, "']'");
        RejectLineCommentAfterElement();
        GqlPatternDirection direction;
        if (Take(TokenType.RightArrow)) { direction = pointsLeft ? GqlPatternDirection.LeftOrRight : GqlPatternDirection.Outgoing; }
        else
        {
            Expect(TokenType.Minus, "'-' or '->'");
            direction = pointsLeft ? GqlPatternDirection.Incoming : GqlPatternDirection.Undirected;
        }
        return new GqlRelationshipPattern(variable, (labels as GqlLabelName)?.Name, direction, properties) { LabelExpression = labels, Quantifier = quantifier };
    }

    /// <summary>
    /// An abbreviated edge has no filler: ISO/IEC 39075 gives it no variable, label expression or
    /// property map, so <c>-r-&gt;</c>, <c>-:T-&gt;</c>, <c>-&gt;[r]</c> and <c>-{k: 1}-&gt;</c> fail
    /// here rather than at a less helpful token.
    /// </summary>
    private GqlRelationshipPattern AbbreviatedEdge(GqlPatternDirection direction)
    {
        if (!Failed && (IsVariableToken || Current.Type is TokenType.Colon or
            TokenType.LeftBracket || Current.Type == TokenType.LeftBrace && !IsQuantifier || Current.Type == TokenType.Keyword && Is("IS")))
        {
            Error("CG00002", ErrorMessages.GqlQueryParserAbbreviatedEdgeVoid, Current);
        }
        return new GqlRelationshipPattern(null, null, direction, _noProperties);
    }

    /// <summary>
    /// ISO/IEC 39075 reads <c>--</c> as a simple comment, so the Cypher arrows <c>--&gt;</c> and
    /// <c>--</c> written directly after a node's <c>)</c> or an edge's <c>]</c> would silently
    /// hide the rest of the line. A comment that begins at exactly that offset is
    /// <c>CG00008</c>; one separated by whitespace keeps its ISO meaning.
    /// </summary>
    private void RejectLineCommentAfterElement()
    {
        if (Failed || !_lineComments.TryGetValue(Previous.End, out var comment)) { return; }
        Error("CG00008", ErrorMessages.RejectLineCommentAfterElementVoid, comment with { Text = "--", End = comment.Start + 2 });
    }

    private IReadOnlyDictionary<string, GqlExpression> ParseProperties()
    {
        Dictionary<string, GqlExpression> properties = new(StringComparer.Ordinal);
        if (Take(TokenType.LeftBrace))
        {
            if (Current.Type != TokenType.RightBrace)
            {
                do
                {
                    var start = Current;
                    string name = Identifier(allowKeyword: true);
                    Expect(TokenType.Colon, "':'");
                    if (Failed) { break; }
                    if (Current.Type is TokenType.LeftBracket or TokenType.LeftBrace)
                    { _ = ParseScalar(); break; }
                    var value = ParsePredicate();
                    if (!properties.TryAdd(name, value)) { Error("CG00006", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParsePropertiesVoid, name), start); }
                } while (!Failed && Take(TokenType.Comma));
            }
            Expect(TokenType.RightBrace, "'}'");
        }
        return new ReadOnlyDictionary<string, GqlExpression>(properties);
    }
}

