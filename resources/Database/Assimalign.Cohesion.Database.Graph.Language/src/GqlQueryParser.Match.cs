using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseQuery()
    {
        var start = Current;
        var stages = new List<GqlStage>();
        bool segmentWrites = false;
        bool hasNamedBinding = false;
        while (!Failed && Current.Type is not (TokenType.Eof or TokenType.Semicolon))
        {
            var clauseStart = Current;
            if (Take("NEXT"))
            {
                if (stages.Count == 0 || stages[^1] is GqlNextStage or GqlReturnStage { IsWith: true } || stages[^1] is not GqlReturnStage && !segmentWrites || Current.Type is TokenType.Eof or TokenType.Semicolon)
                { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid, clauseStart); break; }
                if (stages[^1] is not GqlReturnStage) { hasNamedBinding = false; }
                stages.Add(new GqlNextStage(Span(clauseStart, Previous)));
                segmentWrites = false;
                continue;
            }
            if (stages.Count != 0 && stages[^1] is GqlReturnStage { IsWith: false }) { break; }
            if (Is("MATCH") || Is("OPTIONAL"))
            {
                bool optional = Take("OPTIONAL");
                Expect("MATCH");
                var paths = Failed ? [] : ParsePatterns(allowPathVariable: true);
                GqlExpression? predicate = null;
                if (!Failed && Take("WHERE")) { predicate = ParsePredicate(); }
                hasNamedBinding |= HasNamedBinding(paths);
                stages.Add(new GqlMatchStage(new GqlMatchClause(paths, predicate, optional), Span(clauseStart, Previous)));
            }
            else if (Take("MERGE"))
            {
                var paths = ParsePatterns();
                if (!Failed && paths.Count != 1) { Error("CG00002", ErrorMessages.MergeSinglePath, clauseStart); }
                var onCreate = new List<GqlSetItem>();
                var onMatch = new List<GqlSetItem>();
                while (!Failed && Take("ON"))
                {
                    var branch = Current;
                    bool create = Take("CREATE");
                    if (!create) { Expect("MATCH"); }
                    Expect("SET");
                    if (Failed) { break; }
                    var set = ParseSet(branch);
                    (create ? onCreate : onMatch).AddRange(set.Items);
                }
                hasNamedBinding |= HasNamedBinding(paths);
                if (!Failed) { stages.Add(new GqlMergeStage(paths[0], onCreate.AsReadOnly(), onMatch.AsReadOnly(), Span(clauseStart, Previous))); }
                segmentWrites = true;
            }
            else if (Is("CREATE") || Is("INSERT"))
            {
                bool create = Take("CREATE");
                if (!create) { Advance(); }
                var paths = ParsePatterns();
                hasNamedBinding |= HasNamedBinding(paths);
                stages.Add(new GqlInsertStage(paths, create, Span(clauseStart, Previous)));
                segmentWrites = true;
            }
            else if (Is("DETACH") || Is("DELETE"))
            {
                bool detach = Take("DETACH");
                Expect("DELETE");
                var deletes = new List<string>();
                do { deletes.Add(Identifier()); } while (!Failed && Take(TokenType.Comma));
                stages.Add(new GqlDeleteStage(deletes.AsReadOnly(), detach, Span(clauseStart, Previous)));
                segmentWrites = true;
            }
            else if (Take("SET"))
            {
                stages.Add(ParseSet(clauseStart));
                segmentWrites = true;
            }
            else if (Take("REMOVE"))
            {
                var items = new List<GqlRemoveItem>();
                do
                {
                    var startItem = Current;
                    string variable = Identifier();
                    if (Take(TokenType.Dot))
                    { items.Add(new GqlRemovePropertyItem(variable, Identifier(allowKeyword: true), Span(startItem, Previous))); }
                    else
                    {
                        bool colon = Take(TokenType.Colon);
                        if (!colon) { Expect("IS"); }
                        var labels = new List<string> { Identifier(allowKeyword: true) };
                        while (!Failed && colon && Take(TokenType.Colon)) { labels.Add(Identifier(allowKeyword: true)); }
                        items.Add(new GqlRemoveLabelsItem(variable, labels.AsReadOnly(), colon, Span(startItem, Previous)));
                    }
                } while (!Failed && Take(TokenType.Comma));
                stages.Add(new GqlRemoveStage(items.AsReadOnly(), Span(clauseStart, Previous)));
                segmentWrites = true;
            }
            else if (Take("LET"))
            {
                var bindings = new List<GqlProjection>();
                do
                {
                    string variable = Identifier();
                    Expect(TokenType.Equals, "'='");
                    var expression = ParsePredicate();
                    bindings.Add(new GqlProjection(expression, variable));
                } while (!Failed && Take(TokenType.Comma));
                stages.Add(new GqlLetStage(bindings.AsReadOnly(), Span(clauseStart, Previous)));
                hasNamedBinding = true;
            }
            else if (Take("FILTER"))
            {
                Take("WHERE");
                var predicate = ParsePredicate();
                stages.Add(new GqlFilterStage(predicate, Span(clauseStart, Previous)));
            }
            else if (Is("FOR") || Is("UNWIND"))
            {
                bool unwind = Take("UNWIND");
                string variable;
                GqlExpression list;
                if (unwind)
                { list = ParsePredicate(); Expect("AS"); variable = Failed ? string.Empty : Identifier(); }
                else
                { Advance(); variable = Identifier(); Expect("IN"); list = ParsePredicate(); }
                stages.Add(new GqlForStage(variable, list, unwind, Span(clauseStart, Previous)));
                hasNamedBinding = true;
                if (!Failed && Is("WITH") && (IsWordAt(_position + 1, "ORDINALITY") || IsWordAt(_position + 1, "OFFSET")))
                { Unsupported("FOR ORDINALITY/OFFSET SUFFIX", Current); }
            }
            else if (Is("RETURN") || Is("WITH"))
            {
                if (stages.Count != 0 && stages[^1] is GqlDeleteStage)
                { Unsupported("RETURN AFTER DELETE", Current); break; }
                bool with = Take("WITH");
                if (!with) { Advance(); }
                var projections = new List<GqlProjection>();
                bool returnStar = false;
                var orderBy = new List<GqlSortItem>();
                List<GqlExpression>? groupBy = null;
                GqlExpression? offset = null;
                GqlExpression? limit = null;
                var quantifier = GqlSetQuantifier.All;
                if (Take("DISTINCT")) { quantifier = GqlSetQuantifier.Distinct; }
                else { TakeSetQuantifierAll(); }
                returnStar = Take(TokenType.Asterisk);
                if (returnStar && !hasNamedBinding)
                { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid2, Previous); }
                if (!returnStar)
                {
                    do
                    {
                        int first = _position;
                        var expression = ParsePredicate();
                        expression.SetStatementText(NormalizeProjectionText(first, _position));
                        if (!Failed && Current.Type == TokenType.LeftBrace) { Unsupported("MAP PROJECTION", Current); }
                        string? alias = Take("AS") ? Identifier() : null;
                        projections.Add(new GqlProjection(expression, alias));
                    } while (!Failed && Take(TokenType.Comma));
                }
                if (!Failed && Take("GROUP"))
                {
                    Expect("BY");
                    groupBy = [];
                    if (Current.Type == TokenType.LeftParen && _tokens[_position + 1].Type == TokenType.RightParen)
                    { Advance(); Advance(); }
                    else { do { groupBy.Add(ParsePredicate()); } while (!Failed && Take(TokenType.Comma)); }
                }
                if (!Failed && Take("ORDER"))
                {
                    Expect("BY");
                    do
                    {
                        var expression = ParsePredicate();
                        var direction = Take("DESC") || Take("DESCENDING") ? GqlSortDirection.Descending : GqlSortDirection.Ascending;
                        if (direction == GqlSortDirection.Ascending) { if (!Take("ASC")) { Take("ASCENDING"); } }
                        var nullOrdering = GqlNullOrdering.Default;
                        if (Take("NULLS"))
                        {
                            if (Take("FIRST")) { nullOrdering = GqlNullOrdering.First; }
                            else if (Take("LAST")) { nullOrdering = GqlNullOrdering.Last; }
                            else { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid3, Current); }
                        }
                        orderBy.Add(new GqlSortItem(expression, direction, nullOrdering));
                    } while (!Failed && Take(TokenType.Comma));
                }
                while (!Failed && (Is("OFFSET") || Is("SKIP") || Is("LIMIT")))
                {
                    bool limiting = Is("LIMIT");
                    if (limit is not null || !limiting && offset is not null)
                    { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid4, Current); break; }
                    Advance();
                    var value = ParsePageValue();
                    if (limiting) { limit = value; } else { offset = value; }
                }

                GqlExpression? predicate = null;
                if (!Failed && with && Take("WHERE")) { predicate = ParsePredicate(); }
                stages.Add(new GqlReturnStage(projections.AsReadOnly(), with, Span(clauseStart, Previous))
                { SetQuantifier = quantifier, ReturnStar = returnStar, GroupBy = groupBy?.AsReadOnly(),
                    OrderBy = orderBy.AsReadOnly(), Offset = offset, Limit = limit, Predicate = predicate });
                if (!returnStar)
                { hasNamedBinding = projections.Exists(item => item.Alias is not null || item.Expression is GqlVariableExpression); }
            }
            else if (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT"))
            { Unsupported("STANDALONE ORDER BY AND PAGE STATEMENT", Current); }
            else { break; }
        }
        if (!Failed && (stages.Count != 0 && stages[^1] is GqlReturnStage { IsWith: true } ||
            !segmentWrites && (stages.Count == 0 || stages[^1] is not GqlReturnStage { IsWith: false })))
        { Error("CG00002", ErrorMessages.GqlQueryParserParseQueryVoid5, Current); }
        return new GqlQueryExpression(stages.AsReadOnly(), Span(start, Previous));
    }

    private void RejectCompositePropertyValue(GqlExpression value)
    {
        // Decide from the completed expression: indexing/field access can produce a scalar.
        if (value is GqlListExpression or GqlMapExpression or GqlListComprehensionExpression or GqlListSliceExpression)
        {
            var location = value.Location ?? Span(Current, Current);
            _diagnostics.Add(new Diagnostic
            {
                Code = "CK00001", Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
                Message = value is GqlMapExpression ? ErrorMessages.MapPropertyUnsupported : ErrorMessages.ListPropertyUnsupported,
                Start = location.Start, End = location.End, Line = location.StartLine,
            });
        }
    }

    private GqlExpression ParsePageValue()
    {
        if (Current.Type is TokenType.Integer or TokenType.Parameter or TokenType.Colon || Current.Text == "?") { return ParseScalar(); }
        Error("CG00002", ErrorMessages.GqlQueryParserParsePageValueVoid, Current);
        return new GqlLiteralExpression(null, Span(Current, Current));
    }

    private static bool HasNamedBinding(IReadOnlyList<GqlPathPattern> paths)
    {
        var pending = new Stack<GqlPathPattern>(paths);
        while (pending.TryPop(out var path))
        {
            if (path.Variable is not null) { return true; }
            foreach (var node in path.Nodes) { if (node.Variable is not null) { return true; } }
            foreach (var edge in path.Relationships) { if (edge.Variable is not null) { return true; } }
            foreach (var element in path.Elements ?? [])
            {
                if (element is GqlPathNode { Pattern.Variable: not null } or GqlPathRelationship { Pattern.Variable: not null }) { return true; }
                if (element is GqlParenthesizedPathPattern group) { pending.Push(group.Pattern); }
            }
        }
        return false;
    }

    // Normalize token spacing, preserving spelling, quoted text and grouping. Comments
    // are absent from _tokens. A property reference keeps its familiar variable.property name.
    private string NormalizeProjectionText(int first, int end)
    {
        var text = new System.Text.StringBuilder();
        for (int i = first; i < end; i++)
        {
            var token = _tokens[i];
            if (i > first && token.Type is not (TokenType.Dot or TokenType.RightParen or TokenType.RightBracket or TokenType.Comma) &&
                !(token.Type == TokenType.LeftParen && _tokens[i - 1].Type == TokenType.Function) &&
                _tokens[i - 1].Type is not (TokenType.Dot or TokenType.LeftParen or TokenType.LeftBracket)) { text.Append(' '); }
            // Scalar parsing splits a lexical <- into < and unary - in place. Restore the
            // comparison in its column name without inserting tokens (quadratic for chains).
            if (token.Type == TokenType.Minus && token.Start > 0 && _source[token.Start - 1] == '<') { text.Append("< "); }
            text.Append(token.Text);
        }
        return text.ToString();
    }
    private GqlSetStage ParseSet(Lexeme clauseStart)
    {
        var items = new List<GqlSetItem>();
        do
        {
            var startItem = Current;
            string variable = Identifier();
            if (Take(TokenType.Dot))
            {
                string property = Identifier(allowKeyword: true);
                Expect(TokenType.Equals, "'='");
                var value = ParsePredicate();
                if (!Failed) { RejectCompositePropertyValue(value); }
                items.Add(new GqlSetPropertyItem(variable, property, value, Span(startItem, Previous)));
            }
            else if (Is("IS") || Current.Type == TokenType.Colon)
            {
                bool colon = Take(TokenType.Colon);
                if (!colon) { Advance(); }
                var labels = new List<string> { Identifier(allowKeyword: true) };
                while (!Failed && colon && Take(TokenType.Colon)) { labels.Add(Identifier(allowKeyword: true)); }
                items.Add(new GqlSetLabelsItem(variable, labels.AsReadOnly(), colon, Span(startItem, Previous)));
            }
            else
            {
                bool merge = Current.Type == TokenType.Unrecognized && Current.Text == "+=";
                if (merge) { Advance(); } else { Expect(TokenType.Equals, "'=', '+=', '.', ':' or IS"); }
                if (Failed) { break; }
                if (Current.Type != TokenType.LeftBrace)
                {
                    if (Current.Type == TokenType.Parameter) { RejectUnsupportedParameter("WHOLE-MAP PARAMETER"); }
                    else { Unsupported("WHOLE-MAP EXPRESSION", Current); }
                    break;
                }
                var properties = ParseProperties();
                items.Add(new GqlSetPropertiesItem(variable, properties, merge, Span(startItem, Previous)));
            }
        } while (!Failed && Take(TokenType.Comma));
        return new GqlSetStage(items.AsReadOnly(), Span(clauseStart, Previous));
    }

}
