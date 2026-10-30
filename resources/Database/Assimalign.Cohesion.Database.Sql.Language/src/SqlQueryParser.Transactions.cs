using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private static bool StartsSetTransaction(TokenLexer lexer)
        => MoveTypeLookahead(ref lexer) && IsWord(ref lexer, "TRANSACTION");

    private void ParseTransactionModes(ref TokenLexer lexer, List<SqlTransactionIsolationLevel> levels, bool required)
    {
        while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            int start = lexer.Current.Position;
            if (IsWord(ref lexer, "ISOLATION"))
            {
                required = true;
                RequireSchemaClause(ref lexer, SqlClauses.IsolationLevel);
                Advance(ref lexer);
                if (!IsWord(ref lexer, "LEVEL")) { break; }
                Advance(ref lexer);
                SqlTransactionIsolationLevel level;
                if (IsWord(ref lexer, "SERIALIZABLE")) { level = SqlTransactionIsolationLevel.Serializable; Advance(ref lexer); }
                else if (IsWord(ref lexer, "REPEATABLE"))
                {
                    Advance(ref lexer);
                    if (!IsWord(ref lexer, "READ")) { break; }
                    level = SqlTransactionIsolationLevel.RepeatableRead;
                    Advance(ref lexer);
                }
                else if (IsWord(ref lexer, "READ"))
                {
                    Advance(ref lexer);
                    if (IsWord(ref lexer, "COMMITTED")) { level = SqlTransactionIsolationLevel.ReadCommitted; }
                    else if (IsWord(ref lexer, "UNCOMMITTED")) { level = SqlTransactionIsolationLevel.ReadUncommitted; }
                    else { break; }
                    Advance(ref lexer);
                }
                else { break; }
                levels.Add(level);
                required = false;
            }
            else if (IsWord(ref lexer, "READ") || IsWord(ref lexer, "NOT") || IsWord(ref lexer, "DEFERRABLE"))
            {
                string form = CurrentText(ref lexer).ToUpperInvariant();
                Advance(ref lexer);
                if (form != "DEFERRABLE" && !IsAtEnd(ref lexer))
                { form += " " + CurrentText(ref lexer).ToUpperInvariant(); Advance(ref lexer); }
                RequireSkippedConstruct(start, _lastTokenEnd, form);
                ConsumeRemaining(ref lexer);
                return;
            }
            else if (levels.Count == 0 && !required) { return; }
            else { required = true; break; }

            if (lexer.Current.Type == TokenType.Comma)
            { Advance(ref lexer); required = true; }
        }
        if (required || !IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            _parseDiagnostics.Add(new Diagnostic
            {
                Code = "CS00012", Message = ErrorMessages.TransactionIsolationSyntax,
                Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
                Start = lexer.Current.Position, End = lexer.Current.Position + lexer.Current.Value.Length, Line = 1,
            });
            ConsumeRemaining(ref lexer);
        }
    }
}
