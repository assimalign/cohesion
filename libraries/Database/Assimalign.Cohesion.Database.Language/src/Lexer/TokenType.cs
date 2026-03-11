using System;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Classifies lexical tokens produced by <see cref="TokenLexer"/>.
/// Covers operators and punctuation used across SQL, OQL, and GQL.
/// </summary>
public enum TokenType
{
    // ── Literals ────────────────────────────────────────────
    Integer,
    Float,
    String,

    // ── Identifiers & Keywords ─────────────────────────────
    Identifier,
    Keyword,
    Function,
    QuotedIdentifier,   // "delimited identifier"

    // ── Arithmetic Operators ───────────────────────────────
    Plus,               // +
    Minus,              // -
    Asterisk,           // *
    Slash,              // /
    Percent,            // %

    // ── Comparison Operators ───────────────────────────────
    Equals,             // =
    NotEquals,          // <> or !=
    LessThan,           // <
    GreaterThan,        // >
    LessEqual,          // <=
    GreaterEqual,       // >=

    // ── Logical / Bitwise Operators ────────────────────────
    Concat,             // ||
    Ampersand,          // &
    Pipe,               // |
    Bang,               // !
    Tilde,              // ~

    // ── Punctuation ────────────────────────────────────────
    LeftParen,          // (
    RightParen,         // )
    LeftBracket,        // [
    RightBracket,       // ]
    LeftBrace,          // {
    RightBrace,         // }
    Comma,              // ,
    Semicolon,          // ;
    Dot,                // .
    DotDot,             // ..  (range)
    Colon,              // :
    ColonColon,         // ::  (type cast)

    // ── Navigation & Graph Patterns ────────────────────────
    RightArrow,         // ->
    LeftArrow,          // <-

    // ── Special ────────────────────────────────────────────
    Parameter,          // $1, $name, @param
    Comment,            // -- line  or  /* block */

    // ── End of Input ───────────────────────────────────────
    Eof,
}
