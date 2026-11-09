using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlExpression ParseMerge()
    {
        var start = Current;
        Advance();
        Expect("INTO");
        string collection = CollectionName();
        string? alias = MergeAlias("USING");
        RejectMergeSuffix();
        if (!Failed) { Expect("USING"); }
        var sourceStart = Current;
        bool keyspace = Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
            (_tokens[_position + 1].Type is not (TokenType.LeftParen or TokenType.LeftBracket or TokenType.Dot) || Is("COHESION_SCHEMA"));
        string sourceCollection = Failed || !keyspace ? string.Empty : CollectionName();
        OqlExpression? sourceExpression = Failed || keyspace ? null : ParseExpression();
        var sourceImplicitNameToken = Previous;
        if (sourceExpression is OqlPathExpression { Location: { } pathLocation })
        {
            int namePosition = _position - 1;
            while (namePosition > 0 && _tokens[namePosition].End > pathLocation.End) { namePosition--; }
            sourceImplicitNameToken = _tokens[namePosition].Type == TokenType.RightBracket && namePosition > 0
                ? _tokens[namePosition - 1] : _tokens[namePosition];
        }
        string? sourceAlias = Failed ? null : MergeAlias("ON", allowBare: keyspace || sourceExpression is OqlSubqueryExpression);
        var sourceNameToken = sourceAlias is null ? sourceImplicitNameToken : Previous;
        if (!Failed && sourceExpression is OqlSubqueryExpression && sourceAlias is null)
        { Error("CD00015", ErrorMessages.DerivedSourceAliasRequired, sourceStart); }
        string sourceName = sourceAlias ?? (sourceExpression is OqlPathExpression { Segments.Count: > 0 } sourcePath
            ? sourcePath.Segments[^1].Name ?? string.Empty : OqlSourceName.Implicit(sourceCollection));
        if (!Failed && sourceName.Length == 0)
        { Error("CD00002", ErrorMessages.MergeSourceAliasRequired, sourceStart); }
        if (!Failed && sourceName == (alias ?? OqlSourceName.Implicit(collection)))
        { Error("CD00010", ErrorMessages.BindingNameDuplicate, sourceNameToken); }
        RejectMergeSuffix();
        if (!Failed) { Expect("ON"); }
        var onToken = Previous;
        bool primary = !Failed && Take("PRIMARY");
        bool lookup = !Failed && Take("KEY");
        if (primary && !lookup && !Failed) { Expect("KEY"); }
        OqlExpression on = Failed ? new OqlLiteralExpression(null) : ParseExpression();
        if (!Failed && !lookup && (on is not OqlBinaryExpression { Operator: "=", Left: OqlMetaExpression { Member: "id", Arguments.Count: 1 } meta } ||
            meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } receiver || receiver.Segments[0].Name != (alias ?? OqlSourceName.Implicit(collection))))
        { _diagnostics.Add(QueryDiagnostics.UnsupportedClause("MERGE general ON predicate", Profile.Language, Span(onToken, onToken))); }
        OqlMergeUpdateAction? update = null;
        OqlMergeDeleteAction? delete = null;
        OqlMergeInsertAction? insert = null;
        int lastAction = 0;
        while (!Failed && Take("WHEN"))
        {
            var actionStart = Previous;
            bool unmatched = Take("NOT");
            Expect("MATCHED");
            if (Failed) { break; }
            Expect("THEN");
            if (Failed) { break; }
            int action = unmatched && Is("INSERT") ? 3 : !unmatched && Is("DELETE") ? 2 : !unmatched && Is("UPDATE") ? 1 : 0;
            if (action == 0 || action <= lastAction)
            { Error("CD00002", ErrorMessages.MergeActionOrderInvalid, actionStart); break; }
            lastAction = action;
            Advance();
            if (action == 1)
            {
                var assignments = new List<OqlUpdateAssignment>();
                var unset = new List<OqlPathExpression>();
                if (Take("SET"))
                {
                    do
                    {
                        var assignmentStart = Current;
                        var path = ParseDocumentPath();
                        if (Failed) { break; }
                        Expect("=");
                        if (Failed) { break; }
                        var value = ParseExpression();
                        assignments.Add(new OqlUpdateAssignment(path, value, Span(assignmentStart, Previous)));
                    } while (!Failed && Take(TokenType.Comma));
                }
                if (!Failed && Take("UNSET"))
                { do { unset.Add(ParseDocumentPath()); } while (!Failed && Take(TokenType.Comma)); }
                if (!Failed && assignments.Count + unset.Count == 0)
                { Error("CD00002", ErrorMessages.OqlQueryParserParseUpdateVoid, Current); }
                update = new(assignments.AsReadOnly(), unset.AsReadOnly(), MergeWhere());
            }
            else if (action == 2) { delete = new(MergeWhere()); }
            else
            {
                OqlExpression? key = null;
                OqlExpression value = new OqlLiteralExpression(null);
                if (!lookup)
                {
                    Expect(TokenType.LeftParen, "'('");
                    if (!Failed && !Take("KEY")) { RefuseMerge("MERGE INSERT key/value omission"); }
                    if (!Failed) { key = ParseExpression(); }
                    if (!Failed) { RejectMergeSuffix(); }
                    if (!Failed && Current.Type == TokenType.RightParen) { RefuseMerge("MERGE INSERT key/value omission"); }
                    if (!Failed) { Expect(TokenType.Comma, "','"); }
                    if (!Failed && !Take("VALUE")) { RefuseMerge("MERGE INSERT key/value omission"); }
                    if (!Failed) { value = ParseExpression(); }
                    if (!Failed) { RejectMergeSuffix(); }
                    if (!Failed) { Expect(TokenType.RightParen, "')'"); }
                }
                else { value = ParseExpression(); }
                insert = new(key, value, MergeWhere());
            }
        }
        RejectMergeSuffix();
        if (!Failed && lastAction == 0) { Error("CD00002", ErrorMessages.MergeActionsMissing, Current); }
        var returning = ParseReturning();
        RejectMergeSuffix();
        // Recovery expressions can contain incomplete paths or exceed the rendering
        // bound. Do not publish them as a MERGE tree (or pass missing members to it).
        if (Failed) { return EmptyExpression(); }
        var merge = new OqlMergeExpression(collection, alias, new(sourceCollection, sourceAlias, sourceExpression), on, lookup, primary,
            update, delete, insert, returning, Span(start, Previous));
        if (!Failed && !OqlMergeRenderer.IsWithinStatementDepth(merge))
        { Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, start); return EmptyExpression(); }
        return merge;
    }

    private string? MergeAlias(string following, bool allowBare = true)
    {
        if (Failed) { return null; }
        if (Take("AS")) { return Identifier(); }
        return allowBare && !Is(following) && !IsMergeSuffix() &&
            Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function ? Identifier() : null;
    }

    private OqlExpression? MergeWhere() => !Failed && Take("WHERE") ? ParseExpression() : null;

    private bool IsMergeSuffix() => Is("LIMIT") || Is("OFFSET") || Is("OPTIONS") || Is("USE") || IsSourceBindingClause() || Is("KEYS") ||
        Current.Type == TokenType.Comma && _position + 1 < _tokens.Count && IsWord(_tokens[_position + 1], "OPTIONS");

    private void RejectMergeSuffix()
    {
        if (Failed || !IsMergeSuffix()) { return; }
        if (Current.Type == TokenType.Comma) { Advance(); }
        string construct = Current.Text.ToUpperInvariant();
        if ((Is("LEFT") || Is("INNER")) && _position + 1 < _tokens.Count && IsWord(_tokens[_position + 1], "UNNEST"))
        { construct += " UNNEST"; }
        else if (Is("LEFT") && _position + 2 < _tokens.Count && IsWord(_tokens[_position + 1], "OUTER") && IsWord(_tokens[_position + 2], "UNNEST"))
        { construct += " OUTER UNNEST"; }
        RefuseMerge("MERGE " + construct);
    }

    private void RefuseMerge(string construct)
        => _diagnostics.Add(QueryDiagnostics.UnsupportedClause(construct, Profile.Language, Span(Current, Current)));
}
