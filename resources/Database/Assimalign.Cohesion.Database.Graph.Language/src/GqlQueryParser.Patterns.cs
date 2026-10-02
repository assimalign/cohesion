using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private static readonly IReadOnlyDictionary<string, object?> _noProperties =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    private IReadOnlyList<GqlPathPattern> ParsePatterns(bool allowPathVariable = false)
    {
        List<GqlPathPattern> paths = [];
        do
        {
            string? variable = null;
            if (allowPathVariable && Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier)
            {
                variable = Identifier();
                Expect(TokenType.Equals, "'='");
            }
            List<GqlNodePattern> nodes = [ParseNode()];
            List<GqlRelationshipPattern> relationships = [];
            // Every ISO edge that is not a tilde form starts with '-', '<-' or '->'.
            while (!Failed && Current.Type is TokenType.Minus or TokenType.LeftArrow or TokenType.RightArrow)
            {
                if (relationships.Count == 64)
                {
                    Error("GQL0005", "A path pattern cannot exceed 64 relationships.", Current);
                    break;
                }
                relationships.Add(ParseRelationship());
                nodes.Add(ParseNode());
            }
            paths.Add(new GqlPathPattern(nodes.AsReadOnly(), relationships.AsReadOnly()) { Variable = variable });
        } while (!Failed && Take(TokenType.Comma));
        return paths.AsReadOnly();
    }

    private GqlNodePattern ParseNode()
    {
        Expect(TokenType.LeftParen, "'('");
        // After a failure nothing more is read, so one malformed token reports one diagnostic.
        if (Failed) { return new GqlNodePattern(null, [], _noProperties); }
        string? variable = Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier ? Identifier() : null;
        var labels = ParseLabelSpecification(node: true);
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
        string? variable = Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier ? Identifier() : null;
        var labels = ParseLabelSpecification(node: false);
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
        return new GqlRelationshipPattern(variable, (labels as GqlLabelName)?.Name, direction, properties) { LabelExpression = labels };
    }

    /// <summary>
    /// An abbreviated edge has no filler: ISO/IEC 39075 gives it no variable, label expression or
    /// property map, so <c>-r-&gt;</c>, <c>-:T-&gt;</c>, <c>-&gt;[r]</c> and <c>-{k: 1}-&gt;</c> fail
    /// here rather than at a less helpful token.
    /// </summary>
    private GqlRelationshipPattern AbbreviatedEdge(GqlPatternDirection direction)
    {
        if (!Failed && (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Colon or
            TokenType.LeftBrace or TokenType.LeftBracket || Current.Type == TokenType.Keyword && Is("IS")))
        {
            Error("GQL0002", "An abbreviated edge pattern cannot carry a variable, label expression or property map; " +
                "write the bracketed form, such as -[r:TYPE]->.", Current);
        }
        return new GqlRelationshipPattern(null, null, direction, _noProperties);
    }

    /// <summary>
    /// ISO/IEC 39075 reads <c>--</c> as a simple comment, so the Cypher arrows <c>--&gt;</c> and
    /// <c>--</c> written directly after a node's <c>)</c> or an edge's <c>]</c> would silently
    /// hide the rest of the line. A comment that begins at exactly that offset is
    /// <c>GQL0008</c>; one separated by whitespace keeps its ISO meaning.
    /// </summary>
    private void RejectLineCommentAfterElement()
    {
        if (Failed || !_lineComments.TryGetValue(Previous.End, out var comment)) { return; }
        Error("GQL0008", "'--' directly after a node or edge pattern starts a GQL comment that hides the rest of the line; " +
            "write the edge as '->', '<-' or '-' (Cypher '-->', '<--', '--' and '<-->' are not GQL), " +
            "or put a space before the comment.", comment with { Text = "--", End = comment.Start + 2 });
    }

    private IReadOnlyDictionary<string, object?> ParseProperties()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
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
                    var value = ParseLiteral();
                    if (!properties.TryAdd(name, value.Value)) { Error("GQL0006", $"Duplicate property '{name}'.", start); }
                } while (!Failed && Take(TokenType.Comma));
            }
            Expect(TokenType.RightBrace, "'}'");
        }
        return new ReadOnlyDictionary<string, object?>(properties);
    }
}

