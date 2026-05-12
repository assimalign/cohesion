using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// A zero-allocation lexer that tokenizes query statements for SQL, OQL, and GQL.
/// Supports iteration via the <c>foreach</c> pattern.
/// <code>
/// var lexer = new TokenLexer(sql, options);
/// foreach (var token in lexer) { /* ... */ }
/// </code>
/// </summary>
public ref struct TokenLexer
{
    private readonly ReadOnlySpan<char> _source;
    private readonly ReadOnlySpan<string> _keywords;
    private readonly ReadOnlySpan<string> _functions;
    private readonly bool _caseSensitive;

    private int _pos;
    private Token _current;

    public TokenLexer(ReadOnlySpan<char> statement, TokenLexerOptions options)
    {
        _source = statement;
        _keywords = options.Keywords;
        _functions = options.Functions;
        _caseSensitive = options.IsCaseSensitive;
        _pos = 0;
        _current = default;
    }

    /// <summary>
    /// Gets the most recently scanned token.
    /// </summary>
    public readonly Token Current => _current;

    /// <summary>
    /// Returns this instance as the enumerator, enabling <c>foreach</c> usage.
    /// </summary>
    public TokenLexer GetEnumerator() => this;

    /// <summary>
    /// Advances the lexer to the next token.
    /// </summary>
    /// <returns><c>true</c> if a token was scanned; <c>false</c> if the end of input was reached.</returns>
    public bool MoveNext()
    {
        SkipWhitespace();

        if (_pos >= _source.Length)
        {
            _current = new Token(TokenType.Eof, ReadOnlySpan<char>.Empty, _pos);
            return false;
        }

        int start = _pos;
        char ch = _source[_pos];

        // Line comment: --
        if (ch == '-' && Peek(1) == '-')
        {
            _current = ScanLineComment(start);
            return true;
        }

        // Block comment: /* */
        if (ch == '/' && Peek(1) == '*')
        {
            _current = ScanBlockComment(start);
            return true;
        }

        // String literal: 'text'
        if (ch == '\'')
        {
            _current = ScanString(start);
            return true;
        }

        // Quoted identifier: "identifier"
        if (ch == '"')
        {
            _current = ScanQuotedIdentifier(start);
            return true;
        }

        // Numeric literal (including .5 style floats)
        if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(Peek(1))))
        {
            _current = ScanNumber(start);
            return true;
        }

        // Identifier, keyword, or function name
        if (char.IsLetter(ch) || ch == '_')
        {
            _current = ScanIdentifier(start);
            return true;
        }

        // Parameter: $1, $name
        if (ch == '$')
        {
            _current = ScanParameter(start);
            return true;
        }

        // Operators and punctuation
        _current = ScanOperatorOrPunctuation(start);
        return true;
    }

    /// <summary>
    /// Resets the lexer to the beginning of the source input.
    /// </summary>
    public void Reset()
    {
        _pos = 0;
        _current = default;
    }

    // ── Private helpers ────────────────────────────────────────────────

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
        {
            _pos++;
        }
    }

    private char Peek(int offset)
    {
        int index = _pos + offset;
        return (uint)index < (uint)_source.Length ? _source[index] : '\0';
    }

    // ── Comments ───────────────────────────────────────────────────────

    private Token ScanLineComment(int start)
    {
        _pos += 2; // skip --
        while (_pos < _source.Length && _source[_pos] != '\n')
        {
            _pos++;
        }
        return new Token(TokenType.Comment, _source[start.._pos], start);
    }

    private Token ScanBlockComment(int start)
    {
        _pos += 2; // skip /*
        int depth = 1;
        while (_pos < _source.Length && depth > 0)
        {
            if (_source[_pos] == '/' && Peek(1) == '*')
            {
                depth++;
                _pos += 2;
            }
            else if (_source[_pos] == '*' && Peek(1) == '/')
            {
                depth--;
                _pos += 2;
            }
            else
            {
                _pos++;
            }
        }
        return new Token(TokenType.Comment, _source[start.._pos], start);
    }

    // ── Literals ───────────────────────────────────────────────────────

    private Token ScanString(int start)
    {
        _pos++; // skip opening '
        while (_pos < _source.Length)
        {
            if (_source[_pos] == '\'')
            {
                if (Peek(1) == '\'')
                {
                    _pos += 2; // escaped quote ''
                }
                else
                {
                    _pos++; // closing '
                    break;
                }
            }
            else
            {
                _pos++;
            }
        }
        return new Token(TokenType.String, _source[start.._pos], start);
    }

    private Token ScanQuotedIdentifier(int start)
    {
        _pos++; // skip opening "
        while (_pos < _source.Length && _source[_pos] != '"')
        {
            _pos++;
        }
        if (_pos < _source.Length)
        {
            _pos++; // skip closing "
        }
        return new Token(TokenType.QuotedIdentifier, _source[start.._pos], start);
    }

    private Token ScanNumber(int start)
    {
        var type = TokenType.Integer;

        // Handle leading dot (.5 style)
        if (_source[_pos] == '.')
        {
            type = TokenType.Float;
            _pos++;
        }

        // Consume integer digits
        while (_pos < _source.Length && char.IsDigit(_source[_pos]))
        {
            _pos++;
        }

        // Decimal point (only when we haven't already consumed a leading dot
        // and the next char after the dot is a digit – avoids eating the .. operator)
        if (type == TokenType.Integer &&
            _pos < _source.Length &&
            _source[_pos] == '.' &&
            Peek(1) != '.' &&
            char.IsDigit(Peek(1)))
        {
            type = TokenType.Float;
            _pos++; // consume .
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
            {
                _pos++;
            }
        }

        // Scientific notation: 1e10, 2.5E-3
        if (_pos < _source.Length && (_source[_pos] is 'e' or 'E'))
        {
            type = TokenType.Float;
            _pos++;
            if (_pos < _source.Length && (_source[_pos] is '+' or '-'))
            {
                _pos++;
            }
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
            {
                _pos++;
            }
        }

        return new Token(type, _source[start.._pos], start);
    }

    // ── Identifiers / Keywords / Functions ─────────────────────────────

    private Token ScanIdentifier(int start)
    {
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
        {
            _pos++;
        }

        var value = _source[start.._pos];
        var comparison = _caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        for (int i = 0; i < _keywords.Length; i++)
        {
            if (value.Equals(_keywords[i], comparison))
            {
                return new Token(TokenType.Keyword, value, start);
            }
        }

        for (int i = 0; i < _functions.Length; i++)
        {
            if (value.Equals(_functions[i], comparison))
            {
                return new Token(TokenType.Function, value, start);
            }
        }

        return new Token(TokenType.Identifier, value, start);
    }

    // ── Parameters ─────────────────────────────────────────────────────

    private Token ScanParameter(int start)
    {
        _pos++; // skip $
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
        {
            _pos++;
        }
        return new Token(TokenType.Parameter, _source[start.._pos], start);
    }

    // ── Operators & Punctuation ────────────────────────────────────────

    private Token ScanOperatorOrPunctuation(int start)
    {
        char ch = _source[_pos];
        char next = Peek(1);

        switch (ch)
        {
            case '(':
                _pos++;
                return new Token(TokenType.LeftParen, _source[start.._pos], start);

            case ')':
                _pos++;
                return new Token(TokenType.RightParen, _source[start.._pos], start);

            case '[':
                _pos++;
                return new Token(TokenType.LeftBracket, _source[start.._pos], start);

            case ']':
                _pos++;
                return new Token(TokenType.RightBracket, _source[start.._pos], start);

            case '{':
                _pos++;
                return new Token(TokenType.LeftBrace, _source[start.._pos], start);

            case '}':
                _pos++;
                return new Token(TokenType.RightBrace, _source[start.._pos], start);

            case ',':
                _pos++;
                return new Token(TokenType.Comma, _source[start.._pos], start);

            case ';':
                _pos++;
                return new Token(TokenType.Semicolon, _source[start.._pos], start);

            case '~':
                _pos++;
                return new Token(TokenType.Tilde, _source[start.._pos], start);

            case '+':
                _pos++;
                return new Token(TokenType.Plus, _source[start.._pos], start);

            case '*':
                _pos++;
                return new Token(TokenType.Asterisk, _source[start.._pos], start);

            case '%':
                _pos++;
                return new Token(TokenType.Percent, _source[start.._pos], start);

            case '/':
                _pos++;
                return new Token(TokenType.Slash, _source[start.._pos], start);

            case '.':
                if (next == '.')
                {
                    _pos += 2;
                    return new Token(TokenType.DotDot, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.Dot, _source[start.._pos], start);

            case ':':
                if (next == ':')
                {
                    _pos += 2;
                    return new Token(TokenType.ColonColon, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.Colon, _source[start.._pos], start);

            case '=':
                _pos++;
                return new Token(TokenType.Equals, _source[start.._pos], start);

            case '!':
                if (next == '=')
                {
                    _pos += 2;
                    return new Token(TokenType.NotEquals, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.Bang, _source[start.._pos], start);

            case '<':
                if (next == '=')
                {
                    _pos += 2;
                    return new Token(TokenType.LessEqual, _source[start.._pos], start);
                }
                if (next == '>')
                {
                    _pos += 2;
                    return new Token(TokenType.NotEquals, _source[start.._pos], start);
                }
                if (next == '-')
                {
                    _pos += 2;
                    return new Token(TokenType.LeftArrow, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.LessThan, _source[start.._pos], start);

            case '>':
                if (next == '=')
                {
                    _pos += 2;
                    return new Token(TokenType.GreaterEqual, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.GreaterThan, _source[start.._pos], start);

            case '-':
                if (next == '>')
                {
                    _pos += 2;
                    return new Token(TokenType.RightArrow, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.Minus, _source[start.._pos], start);

            case '|':
                if (next == '|')
                {
                    _pos += 2;
                    return new Token(TokenType.Concat, _source[start.._pos], start);
                }
                _pos++;
                return new Token(TokenType.Pipe, _source[start.._pos], start);

            case '&':
                _pos++;
                return new Token(TokenType.Ampersand, _source[start.._pos], start);

            case '@':
                _pos++;
                while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                {
                    _pos++;
                }
                return new Token(TokenType.Parameter, _source[start.._pos], start);

            default:
                // Unrecognised single character – surface it so the parser can report an error.
                _pos++;
                return new Token(TokenType.Identifier, _source[start.._pos], start);
        }
    }

    
}
