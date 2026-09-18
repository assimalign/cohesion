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
        _lastTokenEnd = 0;
        _parseDiagnostics.Clear();

        bool hasUnsupportedClause =
            TryFindUnsupportedClause(lexer, out string unsupportedClause, out Location unsupportedLocation) &&
            !Supports(unsupportedClause);

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

        if (!hasUnsupportedClause && !IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon &&
            expression is SqlCreateTableExpression or SqlAlterTableExpression or SqlDropTableExpression)
        {
            AddSyntaxDiagnostic(ref lexer, "Unexpected token after the DDL statement.");
            ConsumeRemaining(ref lexer);
        }

        // A supported expression parser may deliberately stop at a clause outside
        // this profile. Consume the rest only to retain terminator tracking.
        if (hasUnsupportedClause && !IsAtEnd(ref lexer))
        {
            ConsumeRemaining(ref lexer);
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
    private int _lastTokenEnd;
    private string _sourceText = string.Empty;
    private readonly List<Diagnostic> _parseDiagnostics = [];

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

    private void AddSyntaxDiagnostic(ref TokenLexer lexer, string message)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = "SQL0003",
            Message = message,
            Start = lexer.Current.Position,
            End = lexer.Current.Position + lexer.Current.Value.Length,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });
    }

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
            var tokenLocation = Location.Create(
                1,
                1,
                lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length);

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

                // These shapes have syntax trees but no executor support (#1020-#1021).
                // JOIN shape restrictions are checked while parsing each SELECT, so
                // a supported JOIN cannot hide a later unsupported query clause.
                if (pendingExecutionClause is null)
                {
                    if (token.Equals("BY", StringComparison.OrdinalIgnoreCase) &&
                             previousToken?.Equals("GROUP", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        pendingExecutionClause = SqlClauses.GroupBy;
                        tokenLocation = Location.Create(1, 1, previousPosition, lexer.Current.Position + lexer.Current.Value.Length);
                    }
                    else if (token.Equals("HAVING", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingExecutionClause = SqlClauses.Having;
                    }
                    else if (token.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && previousToken is not null)
                    {
                        // A SELECT after the leading command is a nested query or INSERT ... SELECT.
                        pendingExecutionClause = SqlClauses.Subquery;
                    }

                    if (pendingExecutionClause is not null)
                    {
                        pendingExecutionLocation = tokenLocation;
                    }
                }
            }

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
    {
        clause = token.ToUpperInvariant() switch
        {
            "UNION" => SqlClauses.SetOperation,
            "INTERSECT" => SqlClauses.Intersect,
            "EXCEPT" => SqlClauses.Except,
            "RECURSIVE" => SqlClauses.Recursive,
            "WINDOW" => SqlClauses.Window,
            "FETCH" => SqlClauses.Fetch,
            "OVER" => SqlClauses.Over,
            "PARTITION" => SqlClauses.Partition,
            "RETURNING" => SqlClauses.Returning,
            "TOP" => SqlClauses.Top,
            "NATURAL" => SqlClauses.Natural,
            "USING" => SqlClauses.Using,
            _ => string.Empty,
        };

        return clause.Length > 0;
    }

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
