using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlMergeExpression ParseMerge(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        Advance(ref lexer);
        ExpectKeyword(ref lexer, "INTO", "INTO after MERGE");
        var table = ParseRequiredTableReference(ref lexer, dmlTarget: true);
        ExpectKeyword(ref lexer, "USING", "USING after the MERGE target");
        var relation = ParseRelationReference(ref lexer);
        var source = new SqlFromItem(relation, ParseJoinClauses(ref lexer, mergeSource: true));
        // As in FROM, a recognized unsupported source suffix (for example NATURAL JOIN)
        // keeps its preflight CK00001 without cascading missing ON/WHEN diagnostics.
        if (lexer.Current.Position == _preflightClauseStart)
        {
            return new(table, source, new SqlLiteralExpression("NULL", SqlLiteralType.Null, null), [], null,
                Location.Create(1, 1, start, _lastTokenEnd));
        }
        ExpectKeyword(ref lexer, "ON", "ON after the MERGE source");
        var condition = ParseExpression(ref lexer);
        var actions = new List<SqlMergeWhenClause>();
        while (IsKeyword(ref lexer, "WHEN"))
        {
            int clauseStart = lexer.Current.Position;
            var clauseLexer = lexer;
            Advance(ref lexer);
            bool unmatched = IsKeyword(ref lexer, "NOT");
            if (unmatched) { Advance(ref lexer); }
            ExpectWord(ref lexer, "MATCHED");
            var kind = unmatched ? SqlMergeMatchKind.NotMatchedByTarget : SqlMergeMatchKind.Matched;
            bool explicitTarget = false;
            if (unmatched && IsKeyword(ref lexer, "BY"))
            {
                Advance(ref lexer);
                if (IsWord(ref lexer, "SOURCE")) { kind = SqlMergeMatchKind.NotMatchedBySource; Advance(ref lexer); }
                else { explicitTarget = true; ExpectWord(ref lexer, "TARGET"); }
            }
            SqlExpression? filter = null;
            if (IsKeyword(ref lexer, "AND")) { Advance(ref lexer); filter = ParseExpression(ref lexer); }
            if (!ExpectKeyword(ref lexer, "THEN", "THEN after the MERGE match condition")) { break; }
            var action = SqlMergeAction.DoNothing;
            var assignments = new List<SqlAssignment>();
            IReadOnlyList<string>? columns = null;
            IReadOnlyList<SqlExpression>? values = null;
            bool defaults = false;
            var overriding = SqlIdentityOverride.None;
            if (IsKeyword(ref lexer, "UPDATE") && kind != SqlMergeMatchKind.NotMatchedByTarget)
            {
                action = SqlMergeAction.Update;
                Advance(ref lexer);
                if (ExpectKeyword(ref lexer, "SET", "SET after MERGE UPDATE"))
                {
                    assignments.Add(ParseAssignment(ref lexer));
                    while (lexer.Current.Type == TokenType.Comma) { Advance(ref lexer); assignments.Add(ParseAssignment(ref lexer)); }
                }
            }
            else if (IsKeyword(ref lexer, "DELETE") && kind != SqlMergeMatchKind.NotMatchedByTarget)
            {
                action = SqlMergeAction.Delete;
                Advance(ref lexer);
            }
            else if (IsKeyword(ref lexer, "INSERT") && kind == SqlMergeMatchKind.NotMatchedByTarget)
            {
                action = SqlMergeAction.Insert;
                Advance(ref lexer);
                if (lexer.Current.Type == TokenType.LeftParen) { columns = ParseInsertColumnList(ref lexer); }
                overriding = ParseIdentityOverride(ref lexer);
                if (IsKeyword(ref lexer, "DEFAULT"))
                {
                    Advance(ref lexer);
                    defaults = ExpectKeyword(ref lexer, "VALUES", "VALUES after DEFAULT");
                }
                else if (ExpectKeyword(ref lexer, "VALUES", "VALUES or DEFAULT VALUES after MERGE INSERT"))
                {
                    values = ParseSingleValueRow(ref lexer);
                }
            }
            else if (IsWord(ref lexer, "DO"))
            {
                Advance(ref lexer);
                ExpectWord(ref lexer, "NOTHING");
            }
            else
            {
                AddExpectedDiagnostic(ref lexer, kind == SqlMergeMatchKind.NotMatchedByTarget
                    ? "INSERT or DO NOTHING" : "UPDATE, DELETE or DO NOTHING");
                break;
            }
            // _lastTokenEnd includes lookahead. Revisit only this clause's tokens to retain
            // the last consumed token, excluding the next WHEN/RETURNING and trailing comments.
            int clauseEnd = clauseStart;
            while (clauseLexer.Current.Position < lexer.Current.Position && !IsAtEnd(ref clauseLexer))
            {
                if (clauseLexer.Current.Type != TokenType.Comment) { clauseEnd = CurrentTokenEnd(ref clauseLexer); }
                if (!clauseLexer.MoveNext()) { break; }
            }
            actions.Add(new(kind, filter, action, assignments, columns, values, defaults, explicitTarget,
                Location.Create(1, 1, clauseStart, clauseEnd)) { Overriding = overriding });
        }
        if (actions.Count == 0) { AddExpectedDiagnostic(ref lexer, "at least one WHEN clause"); }
        var returning = ParseReturning(ref lexer, table, sources: [source]);
        return new(table, source, condition, actions, returning, Location.Create(1, 1, start, _lastTokenEnd));
    }
}
