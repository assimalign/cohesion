using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private IReadOnlyList<SqlSelectColumn>? ParseReturning(ref TokenLexer lexer, SqlTableReference table, bool onConflict = false,
        IReadOnlyList<SqlFromItem>? sources = null)
    {
        if (RejectDmlOutput(ref lexer) || !IsKeyword(ref lexer, "RETURNING")) { return null; }
        Advance(ref lexer);
        if (IsWord(ref lexer, "WITH"))
        {
            RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, "RETURNING WITH (...)");
            SkipQueryRemainder(ref lexer);
            return [];
        }

        bool MasksPseudoRelation(string name) => name.Equals(table.Alias ?? table.TableName, StringComparison.OrdinalIgnoreCase) ||
            sources is not null && sources.Any(item => MasksItem(item, name));

        static bool MasksItem(SqlFromItem item, string name)
            => MasksReference(item.Table, name) || item.Joins.Any(join => MasksReference(join.Table, name));

        static bool MasksReference(SqlRelationReference reference, string name)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            // A group alias hides its children; an aliasless group exposes their namespaces.
            if (reference.Alias is { } alias) { return name.Equals(alias, StringComparison.OrdinalIgnoreCase); }
            return reference is SqlJoinedTableReference group ? MasksItem(group.Item, name)
                : reference is SqlTableReference named && name.Equals(named.TableName, StringComparison.OrdinalIgnoreCase);
        }

        // OLD/NEW are contextual pseudo-relations only when a visible relation does not mask them
        // (PostgreSQL src/backend/parser/analyze.c:2773-2786). Keep quoted identifiers ordinary.
        var scan = lexer;
        bool afterDot = false;
        while (!IsAtEnd(ref scan) && scan.Current.Type != TokenType.Semicolon)
        {
            if (scan.Current.Type == TokenType.Comment)
            {
                if (!scan.MoveNext()) { break; }
                continue;
            }
            if (!afterDot && (IsWord(ref scan, "OLD") || IsWord(ref scan, "NEW")) &&
                !MasksPseudoRelation(CurrentText(ref scan)) &&
                IsReturningPseudoRelation(scan))
            {
                RequireSkippedConstruct(scan.Current.Position, scan.Current.Position + scan.Current.Value.Length,
                    $"RETURNING {CurrentText(ref scan).ToUpperInvariant()}.");
            }
            if (onConflict && !afterDot && IsWord(ref scan, "EXCLUDED") &&
                !MasksPseudoRelation(CurrentText(ref scan)) &&
                IsReturningPseudoRelation(scan))
            {
                RequireSkippedConstruct(scan.Current.Position, scan.Current.Position + scan.Current.Value.Length, "EXCLUDED in RETURNING");
            }
            afterDot = scan.Current.Type == TokenType.Dot;
            if (!scan.MoveNext()) { break; }
        }

        if (IsAtEnd(ref lexer)) { AddExpectedDiagnostic(ref lexer, "a RETURNING expression"); return []; }
        var columns = ParseSelectColumns(ref lexer);
        if (IsWord(ref lexer, "INTO"))
        {
            RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, "RETURNING ... INTO");
            SkipQueryRemainder(ref lexer);
        }
        return columns;
    }

    private static bool IsReturningPseudoRelation(TokenLexer lexer)
    {
        // A three-part schema.table.column name does not name an OLD/NEW pseudo-relation.
        while (lexer.MoveNext() && lexer.Current.Type == TokenType.Comment) { }
        if (lexer.Current.Type != TokenType.Dot) { return false; }
        while (lexer.MoveNext() && lexer.Current.Type == TokenType.Comment) { }
        return !TryPeekToken(lexer, out string next, out _) || next != ".";
    }

    private bool RejectDmlOutput(ref TokenLexer lexer)
    {
        if (!IsWord(ref lexer, "OUTPUT")) { return false; }
        RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, "OUTPUT");
        SkipQueryRemainder(ref lexer);
        return true;
    }
}
