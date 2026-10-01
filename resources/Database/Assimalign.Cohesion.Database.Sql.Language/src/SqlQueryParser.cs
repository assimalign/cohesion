using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

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
    protected override QueryLanguageProfile Profile => SqlLanguageProfile.Instance;

    /// <inheritdoc />
    public override QueryStatement Parse(ReadOnlySpan<char> query)
    {
        _sourceText = query.ToString();
        var statement = base.Parse(query);

        // Stamp the raw statement text so downstream consumers (planners, tooling,
        // the wire protocol) can carry the source without re-threading it through
        // every expression constructor.
        if (statement is SqlQueryStatement { SqlExpression: { } expression })
        {
            expression.SetStatementText(query.ToString());
        }

        return statement;
    }

    /// <inheritdoc />
    protected override QueryStatement ParseCore(TokenLexer lexer)
    {
        _sawSemicolon = false;
        _sawUnrecognizedCharacter = false;
        _lastTokenEnd = 0;
        _subqueryDepth = 0;
        _paginationDepth = 0;
        _implicitAlias = null;
        _parseDiagnostics.Clear();

        RejectLexicalErrors(lexer);

        bool hasUnsupportedClause =
            TryFindUnsupportedClause(lexer, out string unsupportedClause, out Location unsupportedLocation) &&
            !Supports(unsupportedClause);

        // Advance to the first non-comment token
        if (!AdvancePastComments(ref lexer))
        {
            var emptyExpr = new SqlQueryExpression(SqlQueryCommandType.Unknown, null, null);
            var emptyStmt = new SqlQueryStatement(emptyExpr);
            foreach (var diagnostic in _parseDiagnostics)
            {
                emptyStmt.AddDiagnostic(diagnostic);
            }
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
        string firstToken = CurrentText(ref lexer);
        TrackToken(ref lexer);
        SqlQueryExpression expression;

        if (lexer.Current.Type == TokenType.Keyword)
        {
            var keyword = lexer.Current.Value;

            if (keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseSelect(ref lexer);
            }
            else if (keyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseInsert(ref lexer);
            }
            else if (keyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseUpdate(ref lexer);
            }
            else if (keyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseDelete(ref lexer);
            }
            else if (keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseCreate(ref lexer);
            }
            else if (keyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseAlterTable(ref lexer);
            }
            else if (keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseDrop(ref lexer);
            }
            else if (keyword.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                     keyword.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) ||
                     keyword.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseTransaction(ref lexer);
            }
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

        // A supported expression parser may deliberately stop at a clause outside
        // this profile, which already carries COHDBL001, so the text from that clause
        // on adds no second diagnostic. Otherwise every statement kind must have
        // consumed its whole text: a leftover token means the parse dropped part of
        // what was written (#1068). That includes text the parser stopped at before
        // it reached the clause, as in DELETE FROM t WHRE id = 1 RETURNING *.
        if (!hasUnsupportedClause || lexer.Current.Position < unsupportedLocation.Start)
        {
            RejectTrailingTokens(ref lexer, expression);
        }

        // Consume the rest only to retain terminator tracking.
        if (!IsAtEnd(ref lexer))
        {
            ConsumeRemaining(ref lexer);
        }

        // A character outside the dialect has no meaning, so the statement keeps only its
        // command type. Recovery may have parsed around the character, for example into a
        // NULL placeholder for WHERE b = ?, and none of that is returned (#1101).
        if (_sawUnrecognizedCharacter)
        {
            expression = new SqlQueryExpression(expression.CommandType, null, expression.Location);
        }

        var statement = new SqlQueryStatement(expression);

        foreach (var diagnostic in _parseDiagnostics)
        {
            statement.AddDiagnostic(diagnostic);
        }

        if (hasUnsupportedClause)
        {
            RequireClause(statement, unsupportedClause, unsupportedLocation);
        }

        // Check for unknown command
        if (expression.CommandType == SqlQueryCommandType.Unknown &&
            (!hasUnsupportedClause || !IsUnsupportedClauseStart(firstToken)))
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
    private bool _sawUnrecognizedCharacter;
    private int _lastTokenEnd;
    private string _sourceText = string.Empty;
    private readonly List<Diagnostic> _parseDiagnostics = [];

    // The last implicit (AS-less) table alias and the offset of the token after it. A
    // leftover token at that offset usually means a misspelled keyword was read as the
    // alias, as in DELETE FROM t WHRE id = 1, and the diagnostic says so.
    private (string Alias, string Table, int NextPosition)? _implicitAlias;

    private SqlTransactionExpression ParseTransaction(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        var command = CurrentText(ref lexer).ToUpperInvariant() switch
        {
            "BEGIN" => SqlQueryCommandType.Begin,
            "COMMIT" => SqlQueryCommandType.Commit,
            _ => SqlQueryCommandType.Rollback,
        };
        Advance(ref lexer);
        if (IsKeyword(ref lexer, "TRANSACTION"))
        {
            Advance(ref lexer);
        }
        if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            AddSyntaxDiagnostic(ref lexer, "Expected the end of the transaction-control statement.");
            ConsumeRemaining(ref lexer);
        }
        return new SqlTransactionExpression(command, Location.Create(1, 1, start, _lastTokenEnd));
    }

    /// <summary>
    /// Reports <c>SQL0003</c> at the current token unless an error already starts there.
    /// Recovery leaves the offending token in place, so the enclosing parsers that next
    /// expect something at it would otherwise report the same mistake again.
    /// </summary>
    private void AddSyntaxDiagnostic(ref TokenLexer lexer, string message)
    {
        if (!HasErrorAt(lexer.Current.Position))
        {
            AddSyntaxDiagnostic(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, message);
        }
    }

    /// <summary>
    /// Reports that <paramref name="what"/> was expected at the current token, naming the
    /// token found instead.
    /// </summary>
    private void AddExpectedDiagnostic(ref TokenLexer lexer, string what)
        => AddSyntaxDiagnostic(ref lexer, IsAtEnd(ref lexer)
            ? $"Expected {what} before the end of the statement."
            : $"Expected {what} but found {DescribeToken(ref lexer)}.");

    /// <summary>
    /// Consumes <paramref name="token"/> when it is current; otherwise reports it as
    /// expected and leaves the current token in place. Closing tokens and keywords used
    /// to be skipped when absent, so text such as <c>WHERE (id = 1</c> executed as written
    /// minus its error (#1068).
    /// </summary>
    private bool Expect(ref TokenLexer lexer, TokenType token, string description)
    {
        if (lexer.Current.Type == token)
        {
            Advance(ref lexer);
            return true;
        }

        AddExpectedDiagnostic(ref lexer, description);
        return false;
    }

    /// <summary>
    /// Requires the <c>)</c> that closes a list. When another token is current, reports it
    /// and skips to the matching <c>)</c>, so one malformed element yields one diagnostic.
    /// The skip never crosses <c>;</c> or a clause keyword, which it leaves for the caller.
    /// </summary>
    private void SkipToClosingParenthesis(ref TokenLexer lexer)
    {
        if (lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
            return;
        }

        AddExpectedDiagnostic(ref lexer, "')'");
        int depth = 0;
        while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon &&
               !(depth == 0 && IsStatementBoundaryKeyword(ref lexer)))
        {
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                depth++;
            }
            else if (lexer.Current.Type == TokenType.RightParen)
            {
                Advance(ref lexer);
                if (depth == 0)
                {
                    return;
                }
                depth--;
                continue;
            }

            Advance(ref lexer);
        }
    }

    /// <summary>Consumes <paramref name="keyword"/> when it is current; otherwise reports it as expected.</summary>
    private bool ExpectKeyword(ref TokenLexer lexer, string keyword, string description)
    {
        if (IsKeyword(ref lexer, keyword))
        {
            Advance(ref lexer);
            return true;
        }

        AddExpectedDiagnostic(ref lexer, description);
        return false;
    }

    /// <summary>
    /// Whether the current token can name a table, index or SET column: an identifier,
    /// a quoted identifier, or a keyword that does not start a clause. A clause keyword
    /// in a name position means the name is missing: <c>SET a = 1, WHERE id = 1</c> once
    /// assigned to a column named WHERE and updated every row.
    /// </summary>
    private static bool IsNameToken(ref TokenLexer lexer)
        => IsIdentifierOrKeyword(ref lexer) && !IsStatementBoundaryKeyword(ref lexer);

    /// <summary>
    /// Whether the current token can start an operand, used to decide whether recovery
    /// parses past an operand or leaves the current token for the enclosing parser.
    /// </summary>
    private static bool CanStartOperand(ref TokenLexer lexer)
        => !IsAtEnd(ref lexer) &&
           lexer.Current.Type is not (TokenType.Semicolon or TokenType.RightParen or TokenType.Comma) &&
           !IsStatementBoundaryKeyword(ref lexer);

    /// <summary>
    /// Reports lexical errors the shared lexer passes through as ordinary tokens. A string
    /// literal, quoted identifier or block comment without its closing delimiter runs to
    /// the end of the text as one token, so the clause it swallowed vanished without a
    /// diagnostic: <c>DELETE FROM t /* WHERE id = 1;</c> deleted every row (#1068). A
    /// character outside the dialect, such as <c>?</c>, <c>#</c> or a zero-width space,
    /// lexes as <see cref="TokenType.Unrecognized"/>. It used to lex as a one-character
    /// identifier that the parser bound as an alias or a column (#1101).
    /// </summary>
    private void RejectLexicalErrors(TokenLexer lexer)
    {
        while (lexer.MoveNext())
        {
            int position = lexer.Current.Position;
            var value = lexer.Current.Value;
            switch (lexer.Current.Type)
            {
                case TokenType.String when !IsTerminatedString(value):
                    AddSyntaxDiagnostic(position, position + 1,
                        "Unterminated string literal: the closing ' is missing, so the literal runs to the end of the text.");
                    break;
                case TokenType.QuotedIdentifier when value.Length < 2 || value[^1] != '"':
                    AddSyntaxDiagnostic(position, position + 1,
                        "Unterminated quoted identifier: the closing \" is missing, so the identifier runs to the end of the text.");
                    break;
                case TokenType.Comment when value.StartsWith("/*", StringComparison.Ordinal) && !IsTerminatedBlockComment(value):
                    AddSyntaxDiagnostic(position, position + 2,
                        "Unterminated block comment: the closing */ is missing, so the comment runs to the end of the text.");
                    break;
                case TokenType.Unrecognized:
                    _sawUnrecognizedCharacter = true;
                    AddSyntaxDiagnostic(position, position + value.Length,
                        $"Unexpected character {DescribeCharacter(value)}; it is not part of the SQL dialect.");
                    break;
            }
        }
    }

    /// <summary>
    /// Names a character as written, or by code point when it is invisible or is a
    /// supplementary character (the lexer keeps a surrogate pair together as one token).
    /// </summary>
    private static string DescribeCharacter(ReadOnlySpan<char> value)
    {
        if (value.Length == 2 && char.IsSurrogatePair(value[0], value[1]))
        {
            return $"U+{char.ConvertToUtf32(value[0], value[1]):X4}";
        }

        return char.IsControl(value[0]) || char.IsSurrogate(value[0]) ||
               char.GetUnicodeCategory(value[0]) is System.Globalization.UnicodeCategory.Format
            ? $"U+{(int)value[0]:X4}"
            : $"'{value.ToString()}'";
    }

    // Mirrors TokenLexer.ScanString: '' is an escaped quote and a lone ' closes the literal.
    private static bool IsTerminatedString(ReadOnlySpan<char> value)
    {
        int index = 1;
        while (index < value.Length)
        {
            if (value[index] != '\'')
            {
                index++;
            }
            else if (index + 1 < value.Length && value[index + 1] == '\'')
            {
                index += 2;
            }
            else
            {
                return index == value.Length - 1;
            }
        }

        return false;
    }

    // Mirrors TokenLexer.ScanBlockComment, which nests /* */ pairs.
    private static bool IsTerminatedBlockComment(ReadOnlySpan<char> value)
    {
        int depth = 1;
        int index = 2;
        while (index < value.Length && depth > 0)
        {
            if (value[index] == '/' && index + 1 < value.Length && value[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (value[index] == '*' && index + 1 < value.Length && value[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return depth == 0;
    }

    private void AddSyntaxDiagnostic(int start, int end, string message)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = "SQL0003",
            Message = message,
            Start = start,
            End = end,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });
    }

    /// <summary>
    /// Whether an error already covers <paramref name="position"/>: it starts there, or
    /// its span includes it. Error recovery leaves the offending token in place, so the
    /// statement-level checks use this to report one problem once rather than again at
    /// the same token, or at the second half of a character the lexer split in two.
    /// </summary>
    private bool HasErrorAt(int position)
    {
        foreach (var diagnostic in _parseDiagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Start is int start &&
                start <= position && position < Math.Max(diagnostic.End ?? start, start + 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Requires the statement to end at the end of the text or at one terminating
    /// <c>;</c>. Recursive descent returns at the first token a branch does not
    /// understand, so anything left here was never parsed: executing what was parsed
    /// would run a different statement from the one written, such as a DELETE whose
    /// misspelled WHERE was dropped (#1068). Text after the terminator is a second
    /// statement, and a request carries exactly one.
    /// </summary>
    private void RejectTrailingTokens(ref TokenLexer lexer, SqlQueryExpression expression)
    {
        if (IsAtEnd(ref lexer))
        {
            return;
        }

        if (lexer.Current.Type != TokenType.Semicolon)
        {
            if (!HasErrorAt(lexer.Current.Position))
            {
                AddSyntaxDiagnostic(ref lexer,
                    $"Unexpected {DescribeToken(ref lexer)} after the end of the {StatementName(expression)} statement.{ImplicitAliasHint(ref lexer)}");
            }

            return;
        }

        if (Advance(ref lexer) && !HasErrorAt(lexer.Current.Position))
        {
            AddSyntaxDiagnostic(ref lexer,
                $"Unexpected {DescribeToken(ref lexer)} after ';'. A request accepts exactly one statement.");
        }
    }

    private string ImplicitAliasHint(ref TokenLexer lexer)
        => _implicitAlias is { } alias && alias.NextPosition == lexer.Current.Position
            ? $" '{alias.Alias}' was read as an alias of table '{alias.Table}'."
            : string.Empty;

    private static string DescribeToken(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer))
        {
            return "end of statement";
        }

        string text = CurrentText(ref lexer);
        if (text.Length > 40)
        {
            text = string.Concat(text.AsSpan(0, 40), "...");
        }

        return lexer.Current.Type == TokenType.String ? $"string literal {text}" : $"'{text}'";
    }

    private static string StatementName(SqlQueryExpression expression) => expression switch
    {
        SqlCreateTableExpression => "CREATE TABLE",
        SqlCreateIndexExpression => "CREATE INDEX",
        SqlDropTableExpression => "DROP TABLE",
        SqlDropIndexExpression => "DROP INDEX",
        _ => expression.CommandType switch
        {
            SqlQueryCommandType.Select => "SELECT",
            SqlQueryCommandType.Insert => "INSERT",
            SqlQueryCommandType.Update => "UPDATE",
            SqlQueryCommandType.Delete => "DELETE",
            SqlQueryCommandType.Alter => "ALTER TABLE",
            SqlQueryCommandType.Create => "CREATE",
            SqlQueryCommandType.Drop => "DROP",
            SqlQueryCommandType.Begin => "BEGIN",
            SqlQueryCommandType.Commit => "COMMIT",
            SqlQueryCommandType.Rollback => "ROLLBACK",
            _ => "SQL",
        },
    };

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

    // Quoting is lexical syntax, not part of an identifier's catalog identity. Keep
    // CurrentText unchanged for command dispatch, literals and diagnostics: a quoted
    // reserved word remains an identifier token and must never become a keyword.
    private static string CurrentIdentifierText(ref TokenLexer lexer)
    {
        var value = lexer.Current.Value;
        return lexer.Current.Type == TokenType.QuotedIdentifier && value.Length >= 2 && value[^1] == '"'
            ? value[1..^1].ToString()
            : CurrentText(ref lexer);
    }

    private static bool IsKeyword(ref TokenLexer lexer, string keyword)
    {
        return lexer.Current.Type == TokenType.Keyword &&
               lexer.Current.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches an unquoted word whether or not the profile lexes it as a keyword, for
    /// positional words such as ESCAPE and UNKNOWN. A quoted identifier never matches.
    /// </summary>
    private static bool IsWord(ref TokenLexer lexer, string word)
    {
        return lexer.Current.Type is TokenType.Identifier or TokenType.Keyword &&
               lexer.Current.Value.Equals(word, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Parses the table a DML statement or JOIN names. A missing name is reported and
    /// yields the <c>?</c> placeholder, which never executes because of that error:
    /// <c>DELETE;</c> once deleted every row of a table quoted as <c>"?"</c>.
    /// </summary>
    private SqlTableReference ParseRequiredTableReference(ref TokenLexer lexer)
    {
        if (IsNameToken(ref lexer))
        {
            return ParseTableReference(ref lexer);
        }

        if (!SkipDerivedTable(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, "a table name");
        }

        return new SqlTableReference("?", null, null);
    }

    /// <summary>
    /// Skips a derived table, <c>(SELECT ...) [AS] alias</c>, for recovery. The clause scan
    /// already reports it as <c>COHDBL001</c>; skipping it keeps the statement's other
    /// clauses in view instead of reporting its opening parenthesis as leftover text.
    /// </summary>
    private bool SkipDerivedTable(ref TokenLexer lexer)
    {
        var next = lexer;
        if (lexer.Current.Type != TokenType.LeftParen || !AdvancePastComments(ref next) || !IsKeyword(ref next, "SELECT"))
        {
            return false;
        }

        int depth = 0;
        do
        {
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                depth++;
            }
            else if (lexer.Current.Type == TokenType.RightParen)
            {
                depth--;
            }
        }
        while (Advance(ref lexer) && depth > 0 && lexer.Current.Type != TokenType.Semicolon);

        if (IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
        }
        if (IsNameToken(ref lexer))
        {
            Advance(ref lexer);
        }

        return true;
    }

    private SqlTableReference ParseTableReference(ref TokenLexer lexer)
    {
        string firstPart = CurrentIdentifierText(ref lexer);
        string? schemaName = null;
        string? alias = null;

        if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
        {
            if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                schemaName = firstPart;
                firstPart = CurrentIdentifierText(ref lexer);
                Advance(ref lexer);
            }
            else
            {
                AddExpectedDiagnostic(ref lexer, "a table name after '.'");
            }
        }

        // Check for alias
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AS"))
        {
            if (Advance(ref lexer) && IsNameToken(ref lexer))
            {
                alias = CurrentIdentifierText(ref lexer);
                Advance(ref lexer);
            }
            else
            {
                AddExpectedDiagnostic(ref lexer, "an alias after AS");
            }
        }
        else if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer) &&
                 !IsStatementBoundaryKeyword(ref lexer))
        {
            alias = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
            _implicitAlias = (alias, firstPart, lexer.Current.Position);
        }

        return new SqlTableReference(firstPart, schemaName, alias);
    }

    private static bool IsStatementBoundaryKeyword(ref TokenLexer lexer)
    {
        if (lexer.Current.Type != TokenType.Keyword)
        {
            return false;
        }

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
               value.Equals("FETCH", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("UNION", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("WINDOW", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OVER", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("PARTITION", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("INTO", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("USING", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("RETURNING", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindUnsupportedClause(
        TokenLexer lexer,
        out string clause,
        out Location location)
    {
        Location? pendingCte = null;
        string? pendingExecutionClause = null;
        Location? pendingExecutionLocation = null;
        string? previousToken = null;
        string? previousPreviousToken = null;
        string? firstToken = null;
        bool insertValues = false;
        int previousPosition = 0;

        while (lexer.MoveNext())
        {
            if (lexer.Current.Type == TokenType.Comment)
            {
                continue;
            }

            if (lexer.Current.Type == TokenType.Semicolon)
            {
                break;
            }

            string token = CurrentText(ref lexer);
            firstToken ??= token;
            var tokenLocation = Location.Create(
                1,
                1,
                lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length);

            if (TryFindUnsupportedSubqueryForm(lexer, firstToken, previousToken,
                previousPreviousToken, insertValues, out clause))
            {
                location = tokenLocation;
                return true;
            }

            insertValues |= lexer.Current.Type == TokenType.Keyword &&
                firstToken.Equals("INSERT", StringComparison.OrdinalIgnoreCase) &&
                token.Equals("VALUES", StringComparison.OrdinalIgnoreCase);

            if (token.Equals("COLLATION", StringComparison.OrdinalIgnoreCase) &&
                previousToken?.Equals("CREATE", StringComparison.OrdinalIgnoreCase) == true)
            {
                clause = "user-defined collations (CREATE COLLATION)";
                location = tokenLocation;
                return true;
            }
            if (firstToken.Equals("SET", StringComparison.OrdinalIgnoreCase) &&
                (token.Equals("COLLATION", StringComparison.OrdinalIgnoreCase) ||
                 token.Equals("COLLATE", StringComparison.OrdinalIgnoreCase)))
            {
                clause = "per-session collation overrides";
                location = tokenLocation;
                return true;
            }
            if (token.Equals("FULLTEXT", StringComparison.OrdinalIgnoreCase) &&
                previousToken?.Equals("CREATE", StringComparison.OrdinalIgnoreCase) == true)
            {
                clause = "collation-aware full-text indexes";
                location = tokenLocation;
                return true;
            }

            if (TryGetUnsupportedAggregateClause(lexer, out clause, out int aggregateEnd))
            {
                location = Location.Create(1, 1, lexer.Current.Position, aggregateEnd);
                return true;
            }

            // Preserve the more specific OVER/PARTITION diagnostic when one follows
            // a window function, while rejecting bare window-function calls too.
            if (pendingExecutionClause is null && IsWindowFunctionCall(lexer))
            {
                pendingExecutionClause = $"window function {token.ToUpperInvariant()}";
                pendingExecutionLocation = tokenLocation;
            }

            // WITH RECURSIVE is one unsupported construct. Prefer the more
            // specific token so callers can distinguish it from an ordinary CTE.
            if (pendingCte is not null)
            {
                if (token.Equals(SqlClauses.Recursive, StringComparison.OrdinalIgnoreCase))
                {
                    clause = SqlClauses.Recursive;
                    location = tokenLocation;
                }
                else
                {
                    clause = SqlClauses.Cte;
                    location = pendingCte;
                }

                return true;
            }

            if (token.Equals(SqlClauses.Cte, StringComparison.OrdinalIgnoreCase))
            {
                pendingCte = tokenLocation;
                continue;
            }

            if (lexer.Current.Type == TokenType.Keyword)
            {
                bool isCreateView =
                    previousToken?.Equals("CREATE", StringComparison.OrdinalIgnoreCase) == true;
                bool isDropView =
                    previousToken?.Equals("DROP", StringComparison.OrdinalIgnoreCase) == true;

                if (token.Equals("VIEW", StringComparison.OrdinalIgnoreCase) &&
                    (isCreateView || isDropView))
                {
                    clause = isCreateView
                        ? SqlClauses.CreateView
                        : SqlClauses.DropView;
                    location = Location.Create(
                        1,
                        1,
                        previousPosition,
                        lexer.Current.Position + lexer.Current.Value.Length);
                    return true;
                }

                if (token.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                    previousToken?.Equals("ON", StringComparison.OrdinalIgnoreCase) == true)
                {
                    clause = "ON UPDATE";
                    location = Location.Create(1, 1, previousPosition, lexer.Current.Position + lexer.Current.Value.Length);
                    return true;
                }

                if (token.Equals(SqlClauses.All, StringComparison.OrdinalIgnoreCase) &&
                    previousToken?.Equals(SqlClauses.Select, StringComparison.OrdinalIgnoreCase) == true)
                {
                    clause = SqlClauses.All;
                    location = tokenLocation;
                    return true;
                }

                if (TryGetUnsupportedClause(token, out clause))
                {
                    location = tokenLocation;
                    return true;
                }

            }

            previousPreviousToken = previousToken;
            previousToken = token;
            previousPosition = lexer.Current.Position;
        }

        if (pendingCte is not null)
        {
            clause = SqlClauses.Cte;
            location = pendingCte;
            return true;
        }

        if (pendingExecutionClause is not null)
        {
            clause = pendingExecutionClause;
            location = pendingExecutionLocation!;
            return true;
        }

        clause = string.Empty;
        location = null!;
        return false;
    }

    private static bool TryGetUnsupportedClause(string token, out string clause)
        => SqlUnsupportedVocabulary.TryGetClause(token, out clause);

    private static bool IsUnsupportedClauseStart(string token) =>
        token.Equals(SqlClauses.Cte, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(SqlClauses.All, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(SqlClauses.UniqueConstraint, StringComparison.OrdinalIgnoreCase) ||
        TryGetUnsupportedClause(token, out _);

    private static bool TryPeekToken(TokenLexer lexer, out string token, out int tokenEnd)
    {
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type == TokenType.Comment)
            {
                continue;
            }

            if (lexer.Current.Type == TokenType.Semicolon)
            {
                break;
            }

            token = CurrentText(ref lexer);
            tokenEnd = lexer.Current.Position + lexer.Current.Value.Length;
            return true;
        }

        token = string.Empty;
        tokenEnd = 0;
        return false;
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

    /// <summary>
    /// Strips the surrounding quotes from a string-literal lexeme and unescapes
    /// doubled quotes, yielding the literal's value.
    /// </summary>
    private static string UnquoteStringLiteral(string lexeme)
    {
        if (lexeme.Length >= 2 && lexeme[0] == '\'' && lexeme[^1] == '\'')
        {
            return lexeme[1..^1].Replace("''", "'");
        }

        return lexeme;
    }
}
