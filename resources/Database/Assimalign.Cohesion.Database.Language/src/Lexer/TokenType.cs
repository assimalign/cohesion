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

    // ── Lexical Errors ─────────────────────────────────────
    // Appended after Eof so every existing member keeps its value.

    /// <summary>
    /// A character no supported language uses, such as <c>?</c>, <c>#</c>, <c>^</c> or <c>§</c>.
    /// It is never an identifier: each parser reports it with its own syntax diagnostic at the
    /// character's span and never binds it as a name. A supplementary character (a surrogate
    /// pair) is one token.
    /// </summary>
    Unrecognized,
}
