using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Parses SQL statements into a rich AST with full clause-level structure.
/// </summary>
/// <remarks>
/// Uses recursive-descent parsing dispatched by the leading keyword.
/// The parser is split into partial class files for maintainability.
/// </remarks>
public sealed partial class SqlQueryParser : QueryParser
{
    /// <summary>
    /// Initializes a new <see cref="SqlQueryParser"/>.
    /// </summary>
    /// <param name="options">Parser options.</param>
    public SqlQueryParser(QueryParserOptions? options = null)
        : base(options ?? new QueryParserOptions())
    {
    }

    /// <inheritdoc />
    protected override TokenLexerOptions Options => TokenLexerOptions.Sql;

    /// <inheritdoc />
    protected override QueryStatement ParseCore(TokenLexer lexer)
    {
        _sawSemicolon = false;
        _lastTokenEnd = 0;

        // Advance to the first non-comment token
        if (!AdvancePastComments(ref lexer))
        {
            var emptyExpr = new SqlQueryExpression(SqlQueryCommandType.Unknown, null, null);
            var emptyStmt = new SqlQueryStatement(emptyExpr);
            emptyStmt.AddDiagnostic(new Diagnostic
            {
                Code = "SQL0001",
                Message = "Query text is empty.",
                Start = 0,
                End = 0,
                Severity = DiagnosticSeverity.Error,
                Location = DiagnosticLocation.Absolute,
            });
            return emptyStmt;
        }

        int firstTokenPosition = lexer.Current.Position;
        TrackToken(ref lexer);
        SqlQueryExpression expression;

        if (lexer.Current.Type == TokenType.Keyword)
        {
            var keyword = lexer.Current.Value;

            if (keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                expression = ParseSelect(ref lexer);
            else if (keyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
                expression = ParseInsert(ref lexer);
            else if (keyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
                expression = ParseUpdate(ref lexer);
            else if (keyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                expression = ParseDelete(ref lexer);
            else if (keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                expression = ParseCreateTable(ref lexer);
            else if (keyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                expression = ParseAlterTable(ref lexer);
            else if (keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase))
                expression = ParseDropTable(ref lexer);
            else
            {
                expression = new SqlQueryExpression(SqlQueryCommandType.Unknown, null,
                    Location.Create(1, 1, firstTokenPosition, firstTokenPosition));
                // consume remaining tokens so we track semicolons
                ConsumeRemaining(ref lexer);
            }
        }
        else
        {
            expression = new SqlQueryExpression(SqlQueryCommandType.Unknown, null,
                Location.Create(1, 1, firstTokenPosition, firstTokenPosition));
            ConsumeRemaining(ref lexer);
        }

        var statement = new SqlQueryStatement(expression);

        // Check for unknown command
        if (expression.CommandType == SqlQueryCommandType.Unknown)
        {
            statement.AddDiagnostic(new Diagnostic
            {
                Code = "SQL0002",
                Message = "Unsupported or unknown SQL command.",
                Start = firstTokenPosition,
                End = firstTokenPosition,
                Severity = DiagnosticSeverity.Error,
                Location = DiagnosticLocation.Absolute,
            });
        }

        // Check for semicolon terminator
        if (!_sawSemicolon)
        {
            statement.AddDiagnostic(new Diagnostic
            {
                Code = "SQL0100",
                Message = "Statement does not end with ';'.",
                Start = _lastTokenEnd,
                End = _lastTokenEnd,
                Severity = DiagnosticSeverity.Information,
                Location = DiagnosticLocation.RelativeEnd,
            });
        }

        return statement;
    }

    // ── Parser state tracked across parse methods ──────────────────────

    private bool _sawSemicolon;
    private int _lastTokenEnd;

    // ── Token navigation helpers ───────────────────────────────────────

    private static bool AdvancePastComments(ref TokenLexer lexer)
    {
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type != TokenType.Comment)
            {
                return true;
            }
        }
        return false;
    }

    private bool Advance(ref TokenLexer lexer)
    {
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type != TokenType.Comment)
            {
                TrackToken(ref lexer);
                return true;
            }
        }
        return false;
    }

    private static string CurrentText(ref TokenLexer lexer)
    {
        return lexer.Current.Type == TokenType.Eof ? string.Empty : lexer.Current.Value.ToString();
    }

    private static bool IsKeyword(ref TokenLexer lexer, string keyword)
    {
        return lexer.Current.Type == TokenType.Keyword &&
               lexer.Current.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeywordOrFunction(ref TokenLexer lexer, string keyword)
    {
        return (lexer.Current.Type == TokenType.Keyword || lexer.Current.Type == TokenType.Function) &&
               lexer.Current.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdentifierOrKeyword(ref TokenLexer lexer)
    {
        return lexer.Current.Type == TokenType.Identifier ||
               lexer.Current.Type == TokenType.Keyword ||
               lexer.Current.Type == TokenType.Function ||
               lexer.Current.Type == TokenType.QuotedIdentifier;
    }

    private static bool IsAtEnd(ref TokenLexer lexer)
    {
        return lexer.Current.Type == TokenType.Eof;
    }

    private SqlTableReference ParseTableReference(ref TokenLexer lexer)
    {
        string firstPart = CurrentText(ref lexer);
        string? schemaName = null;
        string? alias = null;

        if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
        {
            if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                schemaName = firstPart;
                firstPart = CurrentText(ref lexer);
                Advance(ref lexer);
            }
        }

        // Check for alias
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AS"))
        {
            if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                alias = CurrentText(ref lexer);
                Advance(ref lexer);
            }
        }
        else if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer) &&
                 !IsStatementBoundaryKeyword(ref lexer))
        {
            alias = CurrentText(ref lexer);
            Advance(ref lexer);
        }

        return new SqlTableReference(firstPart, schemaName, alias);
    }

    private static bool IsStatementBoundaryKeyword(ref TokenLexer lexer)
    {
        if (lexer.Current.Type != TokenType.Keyword)
            return false;

        var value = lexer.Current.Value;
        return value.Equals("WHERE", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("VALUES", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("FROM", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("JOIN", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("INNER", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("FULL", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("CROSS", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("NATURAL", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("HAVING", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OFFSET", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("UNION", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("INTO", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("RETURNING", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackToken(ref TokenLexer lexer)
    {
        if (lexer.Current.Type != TokenType.Eof)
        {
            _lastTokenEnd = lexer.Current.Position + lexer.Current.Value.Length;
        }
        if (lexer.Current.Type == TokenType.Semicolon)
        {
            _sawSemicolon = true;
        }
    }

    private void ConsumeRemaining(ref TokenLexer lexer)
    {
        while (Advance(ref lexer))
        {
            // just consuming tokens to track semicolon and position
        }
    }
}
