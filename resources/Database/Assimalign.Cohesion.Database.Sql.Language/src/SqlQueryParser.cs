using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Parses SQL statements into a rich AST with full clause-level structure.
/// </summary>
/// <remarks>
/// Uses recursive-descent parsing dispatched by the leading keyword.
/// The parser is split into partial class files for maintainability.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlQueryParser")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryParser.DebugView))]
public sealed partial class SqlQueryParser : QueryParser
{
    /// <summary>
    /// Initializes a new <see cref="SqlQueryParser"/>.
    /// </summary>
    /// <param name="options">
    /// Parser options. A <see cref="SqlQueryParserOptions"/> also sets the expression nesting
    /// limit; any other options, or none, apply
    /// <see cref="SqlQueryParserOptions.DefaultExpressionNestingLimit"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SqlQueryParserOptions.ExpressionNestingLimit"/> is outside
    /// <see cref="SqlQueryParserOptions.MinimumExpressionNestingLimit"/>..<see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/>.
    /// </exception>
    public SqlQueryParser(QueryParserOptions? options = null)
        : base(options ?? new QueryParserOptions())
    {
        _nestingLimit = options is SqlQueryParserOptions sqlOptions
            ? SqlQueryParserOptions.Validate(sqlOptions.ExpressionNestingLimit, nameof(options))
            : SqlQueryParserOptions.DefaultExpressionNestingLimit;
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
        _expressionDepth = 0;
        _parenthesisDepth = 0;
        _deepestNesting = 0;
        _nestingDiagnostic = -1;
        _implicitAlias = null;
        _parseDiagnostics.Clear();

        RejectLexicalErrors(lexer);

        bool hasUnsupportedClause =
            TryFindUnsupportedClause(lexer, out string unsupportedClause, out Location unsupportedLocation);
        _preflightClauseStart = hasUnsupportedClause ? unsupportedLocation.Start : -1;

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
                Code = "CS00001",
                Message = ErrorMessages.SqlQueryParserParseCoreDiagnostic,
                Start = 0,
                End = 0,
                Severity = DiagnosticSeverity.Error,
                Location = DiagnosticLocation.Absolute,
            });
            return emptyStmt;
        }

        // A stray character before the statement, such as a byte order mark that was not
        // stripped, is already reported by RejectLexicalErrors. Dispatching on it reported an
        // unknown command and a bogus "SUBQUERY in ?" clause as well (#1101).
        TrackToken(ref lexer);
        bool skippedStrayCharacter = false;
        while (lexer.Current.Type == TokenType.Unrecognized)
        {
            skippedStrayCharacter = true;
            Advance(ref lexer);
        }

        bool onlyStrayCharacters = skippedStrayCharacter &&
            (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon);
        int firstTokenPosition = lexer.Current.Position;
        string firstToken = CurrentText(ref lexer);
        SqlQueryExpression expression;

        if (IsWord(ref lexer, "SET") && StartsSetTransaction(lexer))
        {
            expression = ParseTransaction(ref lexer);
        }
        else if (IsWord(ref lexer, "DISCARD"))
        {
            RequireSchemaClause(ref lexer, SqlClauses.DiscardAll);
            Advance(ref lexer);
            if (!IsWord(ref lexer, "ALL")) { AddExpectedDiagnostic(ref lexer, "ALL"); }
            else { Advance(ref lexer); }
            expression = new SqlTransactionExpression(SqlQueryCommandType.Discard,
                Location.Create(1, 1, firstTokenPosition, _lastTokenEnd));
        }
        else if (lexer.Current.Type == TokenType.Keyword)
        {
            var keyword = lexer.Current.Value;

            if (keyword.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseWith(ref lexer);
            }
            else if (keyword.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseExplain(ref lexer);
            }
            else if (keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseQueryExpression(ref lexer);
            }
            else if (keyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseInsert(ref lexer);
            }
            else if (keyword.Equals("MERGE", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseMerge(ref lexer);
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
            else if (keyword.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseTruncate(ref lexer);
            }
            else if (keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase))
            {
                expression = ParseDrop(ref lexer);
            }
            else if (keyword.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                     keyword.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) ||
                     keyword.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
                     keyword.Equals("END", StringComparison.OrdinalIgnoreCase))
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
        else if (lexer.Current.Type == TokenType.LeftParen)
        {
            expression = ParseQueryExpression(ref lexer);
        }
        else if (IsWord(ref lexer, "START"))
        {
            // START is contextual so an ordinary column named start remains an identifier.
            expression = ParseTransaction(ref lexer);
        }
        else
        {
            expression = new SqlQueryExpression(SqlQueryCommandType.Unknown, null,
                Location.Create(1, 1, firstTokenPosition, firstTokenPosition));
            ConsumeRemaining(ref lexer);
        }

        // A supported expression parser may deliberately stop at a clause outside
        // this profile, which already carries CK00001, so the text from that clause
        // on adds no second diagnostic. Otherwise every statement kind must have
        // consumed its whole text: a leftover token means the parse dropped part of
        // what was written (#1068). That includes text the parser stopped at before
        // it reached the clause, as in DELETE FROM t WHRE id = 1 WINDOW w AS ().
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

        // A statement that crossed a nesting limit was abandoned at that point (#1151): only
        // its CS00006 and what was reported before it describe the text, and only its command
        // type is kept, so no consumer ever receives the partial tree.
        if (NestingExceeded)
        {
            DropDiagnosticsAfterNestingLimit();
            expression = new SqlQueryExpression(expression.CommandType, null, expression.Location);
        }

        // An engine whose limit is lower than this parser's reads the measure back instead of
        // parsing the statement again (SqlQueryStatement.ExpressionNestingDepth).
        expression.SetNestingDepth(_deepestNesting);
        var statement = new SqlQueryStatement(expression);

        foreach (var diagnostic in _parseDiagnostics)
        {
            statement.AddDiagnostic(diagnostic);
        }

        if (hasUnsupportedClause)
        {
            RequireClause(statement, unsupportedClause, unsupportedLocation);
        }

        // Check for unknown command. Text made only of stray characters has none to name.
        if (expression.CommandType == SqlQueryCommandType.Unknown && !onlyStrayCharacters &&
            (!hasUnsupportedClause || !IsUnsupportedClauseStart(firstToken)))
        {
            statement.AddDiagnostic(new Diagnostic
            {
                Code = "CS00002",
                Message = ErrorMessages.SqlQueryParserParseCoreDiagnostic2,
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
                Code = "CS00009",
                Message = ErrorMessages.SqlQueryParserParseCoreDiagnostic3,
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

    // Where the clause the preflight scan reports starts, or -1. The scan reports only the
    // first unsupported clause; a construct the parser skips for recovery is reported by the
    // parser unless it is that clause (#1101).
    private int _preflightClauseStart = -1;
    private string _sourceText = string.Empty;
    private readonly List<Diagnostic> _parseDiagnostics = [];

    // The last implicit (AS-less) table alias and the offset of the token after it. A
    // leftover token at that offset usually means a misspelled keyword was read as the
    // alias, as in DELETE FROM t WHRE id = 1, and the diagnostic says so.
    private (string Alias, string Table, int NextPosition)? _implicitAlias;

    private SqlTransactionExpression ParseTransaction(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        bool isStart = IsWord(ref lexer, "START");
        bool isSet = IsWord(ref lexer, "SET");
        bool isEnd = IsKeyword(ref lexer, "END");
        var command = isSet ? SqlQueryCommandType.SetTransaction : isStart || IsKeyword(ref lexer, "BEGIN")
            ? SqlQueryCommandType.Begin
            : isEnd || IsKeyword(ref lexer, "COMMIT")
                ? SqlQueryCommandType.Commit
                : SqlQueryCommandType.Rollback;
        Advance(ref lexer);
        if (isStart || isSet)
        {
            ExpectKeyword(ref lexer, "TRANSACTION", isSet ? "TRANSACTION after SET" : "TRANSACTION after START");
        }
        else if (!isEnd && (IsKeyword(ref lexer, "TRANSACTION") || IsWord(ref lexer, "WORK")))
        {
            Advance(ref lexer);
        }
        var levels = new List<SqlTransactionIsolationLevel>();
        if (command is SqlQueryCommandType.Begin or SqlQueryCommandType.SetTransaction)
        {
            if (isSet) { RequireSchemaClause(ref lexer, SqlClauses.SetTransaction); }
            ParseTransactionModes(ref lexer, levels, isSet);
        }
        if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseTransactionVoid);
            ConsumeRemaining(ref lexer);
        }
        return new SqlTransactionExpression(command, Location.Create(1, 1, start, _lastTokenEnd), levels.ToArray());
    }

    /// <summary>
    /// Reports <c>CS00003</c> at the current token unless an error already starts there.
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
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserAddExpectedDiagnosticVoid, what)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserAddExpectedDiagnosticVoid2, what, DescribeToken(ref lexer)));

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
                case TokenType.String when !IsTerminatedQuoted(value):
                    AddSyntaxDiagnostic(position, position + 1,
                        ErrorMessages.SqlQueryParserRejectLexicalErrorsVoid);
                    break;
                case TokenType.QuotedIdentifier when !IsTerminatedQuoted(value):
                    AddSyntaxDiagnostic(position, position + 1,
                        ErrorMessages.SqlQueryParserRejectLexicalErrorsVoid2);
                    break;
                case TokenType.Comment when value.StartsWith("/*", StringComparison.Ordinal) && !IsTerminatedBlockComment(value):
                    AddSyntaxDiagnostic(position, position + 2,
                        ErrorMessages.SqlQueryParserRejectLexicalErrorsVoid3);
                    break;
                case TokenType.Unrecognized:
                    _sawUnrecognizedCharacter = true;
                    AddSyntaxDiagnostic(position, position + value.Length,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserRejectLexicalErrorsVoid4, DescribeCharacter(value)));
                    break;
                case TokenType.Float when value[^1] is 'e' or 'E' or '+' or '-':
                    // The lexer takes an exponent marker without digits into the literal, so
                    // SELECT 1e FROM t parsed and then failed at execution with a raw
                    // FormatException naming no code or span (#1101).
                    AddSyntaxDiagnostic(position, position + value.Length,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserRejectLexicalErrorsVoid5, value.ToString()));
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

    // Mirrors both lexer scans: doubled delimiters escape themselves; a lone one closes.
    private static bool IsTerminatedQuoted(ReadOnlySpan<char> value)
    {
        if (value.Length < 2) { return false; }
        char quote = value[0];
        int index = 1;
        while (index < value.Length)
        {
            if (value[index] != quote)
            {
                index++;
            }
            else if (index + 1 < value.Length && value[index + 1] == quote)
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
            Code = "CS00003",
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
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserRejectTrailingTokensVoid, DescribeToken(ref lexer), StatementName(expression), ImplicitAliasHint(ref lexer)));
            }

            return;
        }

        if (Advance(ref lexer) && !HasErrorAt(lexer.Current.Position))
        {
            AddSyntaxDiagnostic(ref lexer,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserRejectTrailingTokensVoid2, DescribeToken(ref lexer)));
        }
    }

    private string ImplicitAliasHint(ref TokenLexer lexer)
        => _implicitAlias is { } alias && alias.NextPosition == lexer.Current.Position
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorImplicitTableAliasHint, alias.Alias, alias.Table)
            : string.Empty;

    private static string DescribeToken(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer))
        {
            return _errorEndOfStatement;
        }

        string text = CurrentText(ref lexer);
        if (text.Length > 40)
        {
            text = string.Concat(text.AsSpan(0, 40), "...");
        }

        return lexer.Current.Type == TokenType.String ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorStringLiteralTokenDescription, text) : $"'{text}'";
    }

    private static string StatementName(SqlQueryExpression expression) => expression switch
    {
        SqlCreateTableExpression => "CREATE TABLE",
        SqlCreateTableAsExpression => "CREATE TABLE AS",
        SqlCreateSchemaExpression => "CREATE SCHEMA",
        SqlCreateSequenceExpression => SqlClauses.CreateSequence,
        SqlAlterSequenceExpression => SqlClauses.AlterSequence,
        SqlDropSequenceExpression => SqlClauses.DropSequence,
        SqlCreateViewExpression => "CREATE VIEW",
        SqlCreateIndexExpression => "CREATE INDEX",
        SqlDropTableExpression => "DROP TABLE",
        SqlTruncateExpression => "TRUNCATE",
        SqlDropSchemaExpression => "DROP SCHEMA",
        SqlDropViewExpression => "DROP VIEW",
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
            SqlQueryCommandType.SetTransaction => "SET TRANSACTION",
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
            ? value[1..^1].ToString().Replace("\"\"", "\"", StringComparison.Ordinal)
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
    private SqlTableReference ParseRequiredTableReference(ref TokenLexer lexer, bool insertTarget = false, bool dmlTarget = false)
    {
        // LATERAL (SELECT ...) alias used to bind LATERAL as the table name, so the JOIN it
        // followed also reported a missing ON predicate (#1101).
        if (IsWord(ref lexer, "LATERAL") && TryPeekToken(lexer, out string next, out _) && next == "(")
        {
            int start = lexer.Current.Position;
            RequireSkippedConstruct(start, start + lexer.Current.Value.Length, "LATERAL subquery");
            Advance(ref lexer);
            SkipParenthesized(ref lexer);
            SkipTableAlias(ref lexer);
            return new SqlTableReference("?", null, null);
        }

        if (IsNameToken(ref lexer) || !insertTarget && !dmlTarget && IsContextualCommonTableKeyword(ref lexer))
        {
            return ParseTableReference(ref lexer, insertTarget, dmlTarget || insertTarget);
        }

        if (!SkipDerivedTable(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, "a table name");
        }

        return new SqlTableReference("?", null, null);
    }

    /// <summary>
    /// Refuses a derived mutation target, then skips it for recovery. FROM/JOIN use
    /// ParseRelationReference instead. A preflight refusal at the same span is reused.
    /// </summary>
    private bool SkipDerivedTable(ref TokenLexer lexer)
    {
        var next = lexer;
        if (lexer.Current.Type != TokenType.LeftParen || !AdvancePastComments(ref next) || !IsKeyword(ref next, "SELECT"))
        {
            return false;
        }

        RequireSkippedConstruct(next.Current.Position, next.Current.Position + next.Current.Value.Length,
            "derived-table mutation target; use derived queries only in FROM or JOIN");
        SkipParenthesized(ref lexer);
        SkipTableAlias(ref lexer);
        return true;
    }

    /// <summary>
    /// Skips a parenthesized operand from its <c>(</c> through the matching <c>)</c>, for
    /// recovery past a construct already reported. An unbalanced operand is skipped to the
    /// <c>;</c> or the end of the text.
    /// </summary>
    private void SkipParenthesized(ref TokenLexer lexer)
    {
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
    }

    /// <summary>Skips a <c>[AS] alias</c> after a skipped table expression.</summary>
    private void SkipTableAlias(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
        }
        if (IsNameToken(ref lexer))
        {
            Advance(ref lexer);
        }
    }

    /// <summary>Parses one description prefix over an executable data statement.</summary>
    private SqlQueryExpression ParseExplain(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        Advance(ref lexer);
        bool analyze = IsWord(ref lexer, "ANALYZE");
        if (analyze) { Advance(ref lexer); }
        SqlQueryExpression statement;
        if (IsWord(ref lexer, "WITH")) { statement = ParseWith(ref lexer); }
        else if (StartsQuery(lexer, queryExpected: true)) { statement = ParseQueryExpression(ref lexer); }
        else if (IsWord(ref lexer, "INSERT")) { statement = ParseInsert(ref lexer); }
        else if (IsWord(ref lexer, "MERGE")) { statement = ParseMerge(ref lexer); }
        else if (IsWord(ref lexer, "UPDATE")) { statement = ParseUpdate(ref lexer); }
        else if (IsWord(ref lexer, "DELETE")) { statement = ParseDelete(ref lexer); }
        else if (IsSequenceDdlHeader(lexer))
        { statement = IsWord(ref lexer, "CREATE") ? ParseCreate(ref lexer) : IsWord(ref lexer, "ALTER") ? ParseAlterTable(ref lexer) : ParseDrop(ref lexer); }
        else
        {
            RequireSkippedConstruct(start, lexer.Current.Position + lexer.Current.Value.Length,
                $"EXPLAIN {CurrentText(ref lexer)}");
            ConsumeRemaining(ref lexer);
            statement = new SqlQueryExpression(SqlQueryCommandType.Unknown, null, null);
        }
        return new SqlExplainExpression(statement, Location.Create(1, 1, start, _lastTokenEnd), analyze);
    }

    /// <summary>
    /// Reports a construct the parser recognizes and skips for recovery, unless the preflight
    /// clause scan already reports it at <paramref name="start"/>. The scan reports only the
    /// first unsupported clause, so a construct after another one is reported here, once.
    /// </summary>
    private void RequireSkippedConstruct(int start, int end, string construct)
    {
        if (start != _preflightClauseStart && !Supports(construct))
        {
            _parseDiagnostics.Add(QueryDiagnostics.UnsupportedClause(construct, Profile.Language,
                Location.Create(1, 1, start, end)));
        }
    }

    private SqlTableReference ParseTableReference(ref TokenLexer lexer, bool insertTarget = false, bool dmlTarget = false)
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
                 !(insertTarget && IsWord(ref lexer, "OVERRIDING")) &&
                 !(dmlTarget && IsWord(ref lexer, "OUTPUT")) &&
                 !TryReadSelectLockingStrength(lexer, out _) &&
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
        if (IsContextualCommonTableKeyword(ref lexer) && TryPeekToken(lexer, out string qualifier, out _) && qualifier == ".") { return false; }
        if (value.Equals("LEFT", StringComparison.OrdinalIgnoreCase) || value.Equals("RIGHT", StringComparison.OrdinalIgnoreCase))
        {
            if (TryPeekToken(lexer, out string next, out _) && next == "(") { return false; }
        }
        return value.Equals("WHEN", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("THEN", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("WHERE", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("VALUES", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("WITH", StringComparison.OrdinalIgnoreCase) ||
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

    private bool TryFindUnsupportedClause(
        TokenLexer lexer,
        out string clause,
        out Location location)
    {
        bool withStatement = false;
        bool createTable = false;
        bool explainPrefix = false;
        bool inWithDefinitions = false;
        HashSet<string>? cteNames = null;
        string? pendingExecutionClause = null;
        Location? pendingExecutionLocation = null;
        string? previousToken = null;
        string? previousPreviousToken = null;
        string? firstToken = null;
        bool insertValues = false;
        bool dmlWhere = false;
        bool dmlStatement = false;
        bool returning = false;
        bool onConflictUpdate = false;
        bool inFrom = false;
        bool previousFromComma = false;
        bool previousPreviousFromComma = false;
        var fromScopes = new Stack<bool>();
        var callNames = new Stack<string?>();
        bool closedListagg = false;
        int previousPosition = 0;

        while (lexer.MoveNext())
        {
            // A stray character is reported as a lexical error. As the first token it became
            // the statement's command, as in "SUBQUERY in ?" for ? SELECT * FROM t (#1101).
            if (lexer.Current.Type is TokenType.Comment or TokenType.Unrecognized)
            {
                continue;
            }

            if (lexer.Current.Type == TokenType.Semicolon)
            {
                break;
            }

            // WITH in a temporal type or identity option is not a CTE. Declaration parsing
            // validates the syntax; this scan only locates complete lexical suffixes.
            if (TrySkipIdentityOptions(ref lexer, firstToken, previousToken) || TrySkipTimeZoneType(ref lexer))
            {
                previousPreviousToken = previousToken;
                previousToken = CurrentText(ref lexer).ToUpperInvariant();
                previousPosition = lexer.Current.Position;
                continue;
            }

            string token = CurrentText(ref lexer);
            if (firstToken is null && token.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
            {
                explainPrefix = true;
                continue; // Capability checks classify the statement being described.
            }
            if (firstToken is null && explainPrefix && token.Equals("ANALYZE", StringComparison.OrdinalIgnoreCase))
            {
                explainPrefix = false;
                continue;
            }
            firstToken ??= token == "(" ? "SELECT" : token;
            // Namespace words stay contextual. Their dedicated grammar owns the complete
            // clause gate and named exclusions, including CASCADE/RESTRICT independently
            // of the referential-action capabilities.
            if (IsWord(ref lexer, "SCHEMA") && previousToken == firstToken &&
                (firstToken.Equals("CREATE", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("DROP", StringComparison.OrdinalIgnoreCase)))
            {
                clause = string.Empty;
                location = null!;
                return false;
            }
            if (IsWord(ref lexer, "SEQUENCE") && previousToken == firstToken &&
                (firstToken.ToUpperInvariant() is "CREATE" or "ALTER" or "DROP"))
            { clause = string.Empty; location = null!; return false; }
            createTable |= firstToken.Equals("CREATE", StringComparison.OrdinalIgnoreCase) && IsKeyword(ref lexer, "TABLE");
            if (firstToken.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                (IsKeyword(ref lexer, "VIEW") || (createTable && IsKeyword(ref lexer, "AS") && fromScopes.Count == 0)))
            {
                // Dedicated DDL parsing checks the header; AS starts the query's scan.
                while (!IsKeyword(ref lexer, "AS") && lexer.MoveNext())
                {
                    if (IsKeyword(ref lexer, "AS")) { break; }
                }
                firstToken = "SELECT";
                previousToken = null;
                previousPreviousToken = null;
                continue;
            }
            // A WITH-prefixed INSERT's SELECT source can change firstToken below. Retain
            // the outer write identity so its RETURNING subqueries still get their own gate.
            dmlStatement |= fromScopes.Count == 0 &&
                (IsKeyword(ref lexer, "INSERT") || IsKeyword(ref lexer, "UPDATE") || IsKeyword(ref lexer, "DELETE") || IsKeyword(ref lexer, "MERGE"));
            if (fromScopes.Count == 0 && IsWord(ref lexer, "CONFLICT") &&
                string.Equals(previousToken, "ON", StringComparison.OrdinalIgnoreCase))
            {
                insertValues = false;
                inFrom = false;
            }
            if (fromScopes.Count == 0 && IsKeyword(ref lexer, "UPDATE") &&
                string.Equals(previousToken, "DO", StringComparison.OrdinalIgnoreCase))
            {
                onConflictUpdate = true;
                insertValues = false;
                dmlWhere = false;
                inFrom = false;
            }
            if (fromScopes.Count == 0 && IsKeyword(ref lexer, "RETURNING") && dmlStatement)
            {
                returning = true;
                onConflictUpdate = false;
                insertValues = false;
                dmlWhere = false;
                inFrom = false;
            }
            withStatement |= !returning && IsKeyword(ref lexer, "WITH");
            if (!returning && IsKeyword(ref lexer, "WITH")) { inWithDefinitions = true; }
            if (withStatement && !onConflictUpdate && fromScopes.Count == 0 &&
                (IsKeyword(ref lexer, "SELECT") || IsKeyword(ref lexer, "INSERT") ||
                 IsKeyword(ref lexer, "UPDATE") || IsKeyword(ref lexer, "DELETE") || IsKeyword(ref lexer, "MERGE")))
            {
                firstToken = token;
                inWithDefinitions = false;
            }
            if (inWithDefinitions && fromScopes.Count == 0 && IsCommonTableNameToken(ref lexer) &&
                (string.Equals(previousToken, "WITH", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(previousToken, "RECURSIVE", StringComparison.OrdinalIgnoreCase) || previousToken == ","))
            {
                (cteNames ??= new(StringComparer.OrdinalIgnoreCase)).Add(CurrentIdentifierText(ref lexer));
            }
            // An adjacent :name in an operand is one rejected parameter form. Its name
            // belongs to syntax parsing even when it spells a clause or function keyword.
            if (previousToken == ":" && lexer.Current.Position == previousPosition + 1 &&
                lexer.Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function)
            {
                continue;
            }
            var tokenLocation = Location.Create(
                1,
                1,
                lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length);

            if (IsKeyword(ref lexer, "TOP") &&
                (string.Equals(previousToken, "DELETE", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(previousToken, "UPDATE", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(previousToken, "INSERT", StringComparison.OrdinalIgnoreCase)))
            {
                clause = "DML TOP";
                location = tokenLocation;
                return true;
            }

            if (TryFindUnsupportedSubqueryForm(lexer, firstToken.Equals("WITH", StringComparison.OrdinalIgnoreCase) ? "SELECT" : firstToken, previousToken,
                previousPreviousToken, insertValues, dmlWhere, returning, onConflictUpdate,
                previousFromComma || previousPreviousFromComma, out clause))
            {
                location = tokenLocation;
                return true;
            }

            insertValues |= lexer.Current.Type == TokenType.Keyword &&
                firstToken.Equals("INSERT", StringComparison.OrdinalIgnoreCase) &&
                token.Equals("VALUES", StringComparison.OrdinalIgnoreCase);

            // FROM/USING and WHERE admit read subqueries; SET still refuses them.
            dmlWhere |= fromScopes.Count == 0 &&
                (IsKeyword(ref lexer, "FROM") && firstToken.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                 IsKeyword(ref lexer, "USING") && (firstToken.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("MERGE", StringComparison.OrdinalIgnoreCase)));

            dmlWhere |= fromScopes.Count == 0 && IsKeyword(ref lexer, "WHERE") &&
                (firstToken.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("DELETE", StringComparison.OrdinalIgnoreCase));

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

            bool listaggWithin = closedListagg && IsWord(ref lexer, "WITHIN");
            if (lexer.Current.Type == TokenType.LeftParen) { callNames.Push(previousToken); }
            closedListagg = lexer.Current.Type == TokenType.RightParen && callNames.Count > 0 &&
                callNames.Pop() is string closedName && (closedName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase) || closedName == "\"listagg\"");
            if (!listaggWithin && TryGetUnsupportedAggregateClause(lexer, out clause, out int aggregateEnd))
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

            if (IsKeyword(ref lexer, "WITH") && TryPeekToken(lexer, out string withNext, out _) &&
                withNext.Equals("TIES", StringComparison.OrdinalIgnoreCase) && !WithStartsNamedDefinition(lexer))
            {
                clause = "WITH TIES";
                location = tokenLocation;
                return true;
            }
            if (withStatement && previousToken == "(" &&
                (IsKeyword(ref lexer, "INSERT") || IsKeyword(ref lexer, "UPDATE") || IsKeyword(ref lexer, "DELETE") || IsKeyword(ref lexer, "MERGE")))
            {
                clause = $"data-modifying CTE body ({token.ToUpperInvariant()})";
                location = tokenLocation;
                return true;
            }
            if (inWithDefinitions && fromScopes.Count == 0 && (IsWord(ref lexer, "MATERIALIZED") && previousToken?.Equals("AS", StringComparison.OrdinalIgnoreCase) == true ||
                IsWord(ref lexer, "NOT") && previousToken?.Equals("AS", StringComparison.OrdinalIgnoreCase) == true &&
                TryPeekToken(lexer, out string materialized, out _) && materialized.Equals("MATERIALIZED", StringComparison.OrdinalIgnoreCase)))
            {
                clause = "[NOT] MATERIALIZED";
                location = tokenLocation;
                return true;
            }
            if (inWithDefinitions && fromScopes.Count == 0 && previousToken == ")" && (IsWord(ref lexer, "SEARCH") || IsWord(ref lexer, "CYCLE")))
            {
                clause = "SEARCH/CYCLE";
                location = tokenLocation;
                return true;
            }
            if (IsKeyword(ref lexer, "WITH") && previousToken == "(")
            {
                clause = "WITH inside a subquery or derived table";
                location = tokenLocation;
                return true;
            }

            if (lexer.Current.Type == TokenType.Keyword)
            {
                if (token.Equals(SqlClauses.All, StringComparison.OrdinalIgnoreCase) &&
                    previousToken?.Equals(SqlClauses.Select, StringComparison.OrdinalIgnoreCase) == true)
                {
                    clause = SqlClauses.All;
                    location = tokenLocation;
                    return true;
                }

                bool cteIdentifier = cteNames?.Contains(CurrentIdentifierText(ref lexer)) == true && IsCommonTableNameToken(ref lexer) &&
                    !(IsKeyword(ref lexer, "RECURSIVE") && string.Equals(previousToken, "WITH", StringComparison.OrdinalIgnoreCase)) &&
                    (inWithDefinitions && fromScopes.Count == 0 &&
                        (string.Equals(previousToken, "WITH", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(previousToken, "RECURSIVE", StringComparison.OrdinalIgnoreCase) || previousToken == ",") ||
                     string.Equals(previousToken, "FROM", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(previousToken, "USING", StringComparison.OrdinalIgnoreCase) &&
                        fromScopes.Count == 0 && (firstToken.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("MERGE", StringComparison.OrdinalIgnoreCase)) ||
                     string.Equals(previousToken, "JOIN", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(previousToken, "AS", StringComparison.OrdinalIgnoreCase) ||
                     inFrom && previousToken == "," || previousToken == "." ||
                     TryPeekToken(lexer, out string qualifierDot, out _) && qualifierDot == ".");
                if (TryGetUnsupportedClause(token, out clause) && !Supports(clause) && !cteIdentifier)
                {
                    location = tokenLocation;
                    return true;
                }

            }

            // A comma is a relation separator only in a FROM scope. Parentheses
            // suspend that scope, so a select-list comma before a scalar subquery
            // cannot be mistaken for a derived relation (#1109).
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                fromScopes.Push(inFrom);
                inFrom = false;
            }
            else if (lexer.Current.Type == TokenType.RightParen && fromScopes.Count > 0)
            {
                inFrom = fromScopes.Pop();
            }
            else if (lexer.Current.Type == TokenType.Keyword)
            {
                if (token.Equals("FROM", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("USING", StringComparison.OrdinalIgnoreCase) && fromScopes.Count == 0 && (firstToken.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("MERGE", StringComparison.OrdinalIgnoreCase))) { inFrom = true; }
                else if (token.Equals("WHEN", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("ON", StringComparison.OrdinalIgnoreCase) && firstToken.Equals("MERGE", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("WHERE", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("HAVING", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("OFFSET", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("UNION", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)) { inFrom = false; }
            }
            previousPreviousFromComma = previousFromComma;
            previousFromComma = inFrom && lexer.Current.Type == TokenType.Comma;
            previousPreviousToken = previousToken;
            previousToken = token;
            previousPosition = lexer.Current.Position;
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryParser value)
        {
            _ = value;
        }
    }
}
