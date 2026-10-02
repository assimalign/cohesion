using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Documents.Language.Internal;
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
            // Lines break where the lexer ends a line comment (#1150). Counting each gap on its
            // own is exact: a token never starts with LF, so no CR LF straddles two gaps.
            line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned, token.Position - scanned));
            scanned = token.Position;

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
                else if (token.Type == TokenType.Unrecognized)
                {
                    // A character OQL does not use is never a name or an expression, so the
                    // statement is not parsed and nothing is bound (#1101).
                    Error("OQL0002", $"Unexpected character {DescribeCharacter(item.Text)}; it is not part of OQL.", item);
                }
            }
        }
        line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned));

        _tokens.Add(new Lexeme(TokenType.Eof, string.Empty, _source.Length, _source.Length, line));

        // Capability checks precede syntax parsing, so a known unsupported construct
        // cannot be disguised by the first downstream syntax error it would cause.
        var unsupported = FindUnsupported();
        OqlExpression expression;
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
        }
        else if (IsIndexStatement("CREATE"))
        {
            expression = ParseCreateIndex();
        }
        else if (IsIndexStatement("DROP"))
        {
            expression = ParseDropIndex();
        }
        else
        {
            Error("OQL0002", "Expected SELECT, CREATE INDEX, or DROP INDEX.", Current);
            expression = EmptyExpression();
        }

        if (unsupported.Count == 0 && _diagnostics.Count == 0)
        {
            if (Take(TokenType.Semicolon) && Current.Type != TokenType.Eof)
            {
                Error("OQL0002", "Only one statement is accepted per query.", Current);
            }
            else if (Current.Type != TokenType.Eof)
            {
                Error("OQL0002", $"Unexpected token '{Current.Text}'.", Current);
            }
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

    /// <summary>
    /// Finds every recognized construct outside the executable profile, using the
    /// recognized-unsupported table (<see cref="OqlUnsupportedVocabulary"/>). Each construct
    /// is reported once, so a statement whose first word is not OQL reports that word only,
    /// and a quantified predicate reports its quantifier rather than its <c>IN</c>.
    /// </summary>
    private List<(string Clause, Lexeme Token)> FindUnsupported()
    {
        List<(string, Lexeme)> result = [];
        bool selected = false;
        int openQuantifiers = 0;
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

            // A SQL++ quantified predicate, EVERY x IN ... SATISFIES ... END, is one construct:
            // its binding IN is not the IN predicate, and its text up to END belongs to it.
            bool quantifier = value is "EVERY" or "ANY" or "SOME" && IsQuantifierBinding(index + 1);
            if (openQuantifiers > 0)
            {
                if (quantifier)
                {
                    openQuantifiers++;
                    index += 2;
                }
                else if (value == "END")
                {
                    openQuantifiers--;
                }
                continue;
            }
            if (quantifier)
            {
                AddUnsupported(result, $"{value} ... SATISFIES", token);
                openQuantifiers++;
                index += 2;
                continue;
            }

            if (value is "CREATE" or "DROP" && index + 1 < _tokens.Count &&
                string.Equals(_tokens[index + 1].Text, "INDEX", StringComparison.OrdinalIgnoreCase))
            {
                string compound = value == "CREATE" ? OqlClauses.CreateIndex : OqlClauses.DropIndex;
                if (!Supports(compound))
                {
                    var end = _tokens[index + 1];
                    result.Add((compound, new Lexeme(token.Type, compound, token.Start, end.End, token.Line)));
                }
                continue;
            }
            if (value == "SELECT")
            {
                if (selected)
                {
                    result.Add((OqlClauses.Subquery, token));
                }

                selected = true;
                continue;
            }

            // ODMG's quantifiers, FOR ALL x IN e: p and EXISTS x IN e: p, bind with IN too.
            if (value == "FOR" && index + 1 < _tokens.Count && IsWord(_tokens[index + 1], "ALL") &&
                IsQuantifierBinding(index + 2))
            {
                AddUnsupported(result, "FOR ALL", token);
                index += 3;
                continue;
            }
            if (value == "EXISTS" && IsQuantifierBinding(index + 1))
            {
                AddUnsupported(result, "EXISTS", token);
                index += 2;
                continue;
            }

            if (!OqlUnsupportedVocabulary.TryFind(value, out var word) || Supports(word.Construct))
            {
                continue;
            }

            // Words of other languages are not reserved field names: they name a construct
            // only where it can start, so mixed-shape fields such as limit still parse. A
            // statement verb names one only as the first word, so an AS-less FROM alias named
            // merge or upsert still parses, and a quantifier word only through the binding
            // path above, so an alias named every or satisfies does too.
            bool constructPosition = word.Position switch
            {
                OqlWordPosition.Anywhere => true,
                OqlWordPosition.Clause => index == 0 || EndsOperand(index - 1),
                OqlWordPosition.Statement => index == 0,
                _ => false,
            };
            if (!constructPosition)
            {
                continue;
            }

            // UNION ALL and UNION DISTINCT are one set operation, not a set operation plus the
            // ALL or DISTINCT word.
            var reported = token;
            if (value is "UNION" or "INTERSECT" or "EXCEPT" && index + 1 < _tokens.Count &&
                (IsWord(_tokens[index + 1], "ALL") || IsWord(_tokens[index + 1], "DISTINCT")))
            {
                var modifier = _tokens[++index];
                reported = new Lexeme(token.Type, token.Text, token.Start, modifier.End, token.Line);
            }

            result.Add((word.Construct, reported));

            // A statement that does not start as OQL is one unsupported construct; the rest of
            // its text belongs to it (UPDATE c SET ..., MERGE INTO c USING ...).
            if (index == 0)
            {
                break;
            }

            // Each operand of a set operation starts its own SELECT, which is not a subquery.
            if (value is "UNION" or "INTERSECT" or "EXCEPT")
            {
                selected = false;
            }
        }
        return result;
    }

    private void AddUnsupported(List<(string, Lexeme)> result, string construct, Lexeme token)
    {
        if (!Supports(construct))
        {
            result.Add((construct, token));
        }
    }

    /// <summary>
    /// Whether the tokens at <paramref name="index"/> bind a quantifier variable:
    /// <c>name IN</c>, as in <c>EVERY x IN c.items</c> or ODMG's <c>FOR ALL x IN c.items</c>.
    /// </summary>
    private bool IsQuantifierBinding(int index) => index + 1 < _tokens.Count &&
        _tokens[index].Type is TokenType.Identifier or TokenType.QuotedIdentifier &&
        IsWord(_tokens[index + 1], "IN");

    /// <summary>
    /// Whether the token at <paramref name="index"/> ends an operand, so that the word after
    /// it starts a clause: a name, a path segment, a literal, a parameter, a closing bracket,
    /// or <c>ASC</c>/<c>DESC</c>. <c>WHERE a = 1 LIMIT 5</c> and <c>ORDER BY a DESC OFFSET 2</c>
    /// end an operand; <c>SELECT limit</c>, <c>, merge</c> and <c>AS limit</c> do not.
    /// </summary>
    private bool EndsOperand(int index)
    {
        var token = _tokens[index];
        return token.Type switch
        {
            TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function or
            TokenType.RightParen or TokenType.RightBracket or TokenType.Parameter or
            TokenType.Integer or TokenType.Float or TokenType.String => true,
            TokenType.Keyword => index > 0 && _tokens[index - 1].Type == TokenType.Dot ||
                token.Text.ToUpperInvariant() is "NULL" or "NIL" or "TRUE" or "FALSE" or "ASC" or "DESC",
            _ => false,
        };
    }

    private static bool IsWord(Lexeme token, string word) =>
        token.Type is TokenType.Keyword or TokenType.Identifier or TokenType.Function &&
        string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

    private OqlSelectExpression EmptyExpression() => new(string.Empty, null, [], null, [], null, [], Span(Current, Current));
    private bool IsIndexStatement(string verb) => Is(verb) && _position + 1 < _tokens.Count &&
        string.Equals(_tokens[_position + 1].Text, "INDEX", StringComparison.OrdinalIgnoreCase);
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
        Location.Create(start.Line, end.Line + TokenLexer.CountLineBreaks(end.Text), start.Start, end.End);

    private string CollectionName()
    {
        string name = Identifier();
        // This namespace identifies virtual collections in the current database.
        // It does not introduce general database-qualified collection names.
        if (string.Equals(name, "COHESION_SCHEMA", StringComparison.OrdinalIgnoreCase) && Take(TokenType.Dot))
        {
            name += "." + Identifier();
        }
        return name;
    }

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

    private void Error(string code, string message, Lexeme token)
    {
        // Recovery can reach one bad token from several rules, as with SELECT ~a FROM c. It is
        // one mistake, so a second error with the same code at the same span is dropped (#1101).
        foreach (var existing in _diagnostics)
        {
            if (existing.Code == code && existing.Start == token.Start && existing.End == token.End)
            {
                return;
            }
        }

        _diagnostics.Add(new Diagnostic
        {
            Code = code, Message = message, Start = token.Start, End = token.End,
            Line = token.Line, Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
        });
    }

    /// <summary>
    /// Names a character as written, or by code point when it is invisible or is a
    /// supplementary character (the lexer keeps a surrogate pair together as one token).
    /// </summary>
    private static string DescribeCharacter(string text)
    {
        if (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]))
        {
            return $"U+{char.ConvertToUtf32(text[0], text[1]):X4}";
        }

        return char.IsControl(text[0]) || char.IsSurrogate(text[0]) ||
               char.GetUnicodeCategory(text[0]) is UnicodeCategory.Format
            ? $"U+{(int)text[0]:X4}"
            : $"'{text}'";
    }

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
