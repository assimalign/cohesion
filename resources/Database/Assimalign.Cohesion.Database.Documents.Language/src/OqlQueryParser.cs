using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Parses the executable OQL profile into an expression tree with diagnostics.</summary>
/// <remarks>Instances serialize concurrent calls. Offsets are zero-based UTF-16 positions with exclusive ends.</remarks>
public sealed partial class OqlQueryParser : QueryParser
{
    private readonly object _gate = new();
    private string _source = string.Empty;
    private readonly List<Lexeme> _tokens = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private int _position;
    private int _depth;

    /// <summary>Initializes an OQL parser.</summary>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public OqlQueryParser(QueryParserOptions? options = null) : base(options ?? new QueryParserOptions()) { }

    /// <inheritdoc />
    protected override QueryLanguageProfile Profile => OqlLanguageProfile.Instance;

    /// <inheritdoc />
    public override QueryStatement Parse(ReadOnlySpan<char> query)
    {
        lock (_gate)
        {
            _source = query.ToString();
            return base.Parse(query);
        }
    }

    /// <inheritdoc />
    protected override QueryStatement ParseCore(TokenLexer lexer)
    {
        _tokens.Clear();
        _diagnostics.Clear();
        _position = 0;
        _depth = 0;
        int line = 1;
        int scanned = 0;
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            while (scanned < token.Position)
            {
                if (_source[scanned++] == '\n')
                {
                    line++;
                }
            }

            var item = new Lexeme(token.Type, token.Value.ToString(), token.Position,
                token.Position + token.Value.Length, line);
            if (token.Type == TokenType.Comment)
            {
                if (!IsCompleteComment(item.Text))
                {
                    Error("OQL0003", "Unterminated block comment.", item);
                }
            }
            else
            {
                _tokens.Add(item);
                if (token.Type is TokenType.String or TokenType.QuotedIdentifier && !IsCompleteQuoted(item.Text))
                {
                    Error("OQL0003", "Unterminated quoted text.", item);
                }
            }
        }
        while (scanned < _source.Length)
        {
            if (_source[scanned++] == '\n')
            {
                line++;
            }
        }

        _tokens.Add(new Lexeme(TokenType.Eof, string.Empty, _source.Length, _source.Length, line));

        // Capability checks precede syntax parsing, so a known unsupported construct
        // cannot be disguised by the first downstream syntax error it would cause.
        var unsupported = FindUnsupported();
        OqlSelectExpression expression;
        if (unsupported.Count != 0 || _diagnostics.Count != 0)
        {
            expression = EmptyExpression();
        }
        else if (Current.Type == TokenType.Eof)
        {
            Error("OQL0001", "Query text is empty.", Current);
            expression = EmptyExpression();
        }
        else if (Is("SELECT"))
        {
            expression = ParseSelect();
            if (Take(TokenType.Semicolon) && Current.Type != TokenType.Eof)
            {
                Error("OQL0002", "Only one statement is accepted per query.", Current);
            }
            else if (Current.Type != TokenType.Eof)
            {
                Error("OQL0002", $"Unexpected token '{Current.Text}'.", Current);
            }
        }
        else
        {
            Error("OQL0002", "Expected SELECT.", Current);
            expression = EmptyExpression();
        }

        expression.SetStatementText(_source);
        var statement = new OqlQueryStatement(expression);
        foreach (var (clause, token) in unsupported)
        {
            RequireClause(statement, clause, Span(token, token));
        }

        foreach (var diagnostic in _diagnostics)
        {
            statement.AddDiagnostic(diagnostic);
        }

        return statement;
    }

    private List<(string Clause, Lexeme Token)> FindUnsupported()
    {
        List<(string, Lexeme)> result = [];
        bool selected = false;
        for (int index = 0; index < _tokens.Count; index++)
        {
            var token = _tokens[index];
            if (token.Type == TokenType.Semicolon)
            {
                break;
            }

            if (token.Type is not (TokenType.Keyword or TokenType.Function or TokenType.Identifier))
            {
                continue;
            }

            if (index > 0 && _tokens[index - 1].Type == TokenType.Dot)
            {
                continue;
            }

            string value = token.Text.ToUpperInvariant();
            if (value == "SELECT")
            {
                if (selected)
                {
                    result.Add((OqlClauses.Subquery, token));
                }

                selected = true;
                continue;
            }
            string? clause = value switch
            {
                "DEFINE" => OqlClauses.Define,
                "ELEMENT" => OqlClauses.Element,
                "FLATTEN" => OqlClauses.Flatten,
                "DISTINCT" or "ALL" or "IN" or "EXISTS" or "LIKE" or "BETWEEN" or
                "FOR" or "SOME" or "ANY" or "STRUCT" or "LIST" or "SET" or "BAG" or
                "ARRAY" or "COLLECTION" or "FIRST" or "LAST" or "UNIQUE" or "LISTTOSET" or
                "TYPEOF" or "UNDEFINED" or "ABS" => value,
                "CREATE" or "DROP" or "ALTER" or "INSERT" or "UPDATE" or "DELETE" or
                "USE" or "BEGIN" or "COMMIT" or "ROLLBACK" or "WITH" or "JOIN" or
                "LIMIT" or "OFFSET" or "UNION" or "INTERSECT" or "EXCEPT" => value,
                _ => null,
            };
            // Non-OQL command names are not reserved field names. Reject them
            // only in command/clause position, preserving mixed-shape fields.
            if (token.Type == TokenType.Identifier && index > 0 &&
                !IsClausePosition(index))
            {
                clause = null;
            }

            if (clause is not null && !Supports(clause))
            {
                result.Add((clause, token));
            }
        }
        return result;
    }

    private bool IsClausePosition(int index) => index > 0 &&
        _tokens[index - 1].Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.RightParen;

    private OqlSelectExpression EmptyExpression() => new(string.Empty, null, [], null, [], null, [], Span(Current, Current));
    private Lexeme Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private Lexeme Previous => _tokens[Math.Max(0, _position - 1)];
    private bool Failed => _diagnostics.Count != 0;
    private void Advance()
    {
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }
    }
    private bool Is(string text) => Current.Type is not (TokenType.String or TokenType.QuotedIdentifier) &&
        string.Equals(Current.Text, text, StringComparison.OrdinalIgnoreCase);
    private bool Take(string text)
    {
        if (!Is(text))
        {
            return false;
        }
        Advance();
        return true;
    }
    private bool Take(TokenType type)
    {
        if (Current.Type != type)
        {
            return false;
        }
        Advance();
        return true;
    }
    private void Expect(string text)
    {
        if (!Take(text))
        {
            Error("OQL0002", $"Expected {text}.", Current);
        }
    }
    private void Expect(TokenType type, string text)
    {
        if (!Take(type))
        {
            Error("OQL0002", $"Expected {text}.", Current);
        }
    }
    private static Location Span(Lexeme start, Lexeme end) =>
        Location.Create(start.Line, end.Line + end.Text.AsSpan().Count('\n'), start.Start, end.End);

    private string Identifier(bool allowKeyword = false)
    {
        var token = Current;
        if (token.Type == TokenType.QuotedIdentifier)
        {
            Advance();
            if (token.Text.Length == 2)
            {
                Error("OQL0002", "An identifier cannot be empty.", token);
            }

            return token.Text.Length >= 2 ? token.Text[1..^1] : string.Empty;
        }
        if ((token.Type is TokenType.Identifier or TokenType.Function || allowKeyword && token.Type == TokenType.Keyword) &&
            token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_'))
        {
            Advance();
            return token.Text;
        }
        Error("OQL0002", "Expected an identifier.", token);
        return string.Empty;
    }

    private void Error(string code, string message, Lexeme token) => _diagnostics.Add(new Diagnostic
    {
        Code = code, Message = message, Start = token.Start, End = token.End,
        Line = token.Line, Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
    });

    private static bool IsCompleteQuoted(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        char quote = text[0];
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != quote)
            {
                continue;
            }

            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i == text.Length - 1;
        }
        return false;
    }

    private static bool IsCompleteComment(string text)
    {
        if (!text.StartsWith("/*", StringComparison.Ordinal))
        {
            return true;
        }

        int depth = 0;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '/' && text[i + 1] == '*') { depth++; i++; }
            else if (text[i] == '*' && text[i + 1] == '/') { depth--; i++; }
        }
        return depth == 0;
    }

    private readonly record struct Lexeme(TokenType Type, string Text, int Start, int End, int Line);
}
