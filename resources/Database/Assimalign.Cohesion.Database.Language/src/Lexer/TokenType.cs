using System;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Classifies lexical tokens produced by <see cref="TokenLexer"/>.
/// Covers operators and punctuation used across SQL, OQL, and GQL.
/// </summary>
public enum TokenType
{
    // ── Literals ────────────────────────────────────────────
    /// <summary>An integer literal.</summary>
    Integer,
    /// <summary>A floating-point literal.</summary>
    Float,
    /// <summary>A single-quoted string literal.</summary>
    String,

    // ── Identifiers & Keywords ─────────────────────────────
    /// <summary>An unquoted identifier.</summary>
    Identifier,
    /// <summary>A keyword from the language profile.</summary>
    Keyword,
    /// <summary>A function name from the language profile.</summary>
    Function,
    /// <summary>A double-quoted identifier.</summary>
    QuotedIdentifier,   // "delimited identifier"

    // ── Arithmetic Operators ───────────────────────────────
    /// <summary>The <c>+</c> token.</summary>
    Plus,               // +
    /// <summary>The <c>-</c> token.</summary>
    Minus,              // -
    /// <summary>The <c>*</c> token.</summary>
    Asterisk,           // *
    /// <summary>The <c>/</c> token.</summary>
    Slash,              // /
    /// <summary>The <c>%</c> token.</summary>
    Percent,            // %

    // ── Comparison Operators ───────────────────────────────
    /// <summary>The <c>=</c> token.</summary>
    Equals,             // =
    /// <summary>The <c>&lt;&gt; or !=</c> token.</summary>
    NotEquals,          // <> or !=
    /// <summary>The <c>&lt;</c> token.</summary>
    LessThan,           // <
    /// <summary>The <c>&gt;</c> token.</summary>
    GreaterThan,        // >
    /// <summary>The <c>&lt;=</c> token.</summary>
    LessEqual,          // <=
    /// <summary>The <c>&gt;=</c> token.</summary>
    GreaterEqual,       // >=

    // ── Logical / Bitwise Operators ────────────────────────
    /// <summary>The <c>||</c> token.</summary>
    Concat,             // ||
    /// <summary>The <c>&amp;</c> token.</summary>
    Ampersand,          // &
    /// <summary>The <c>|</c> token.</summary>
    Pipe,               // |
    /// <summary>The <c>!</c> token.</summary>
    Bang,               // !
    /// <summary>The <c>~</c> token.</summary>
    Tilde,              // ~

    // ── Punctuation ────────────────────────────────────────
    /// <summary>The <c>(</c> token.</summary>
    LeftParen,          // (
    /// <summary>The <c>)</c> token.</summary>
    RightParen,         // )
    /// <summary>The <c>[</c> token.</summary>
    LeftBracket,        // [
    /// <summary>The <c>]</c> token.</summary>
    RightBracket,       // ]
    /// <summary>The <c>{</c> token.</summary>
    LeftBrace,          // {
    /// <summary>The <c>}</c> token.</summary>
    RightBrace,         // }
    /// <summary>The <c>,</c> token.</summary>
    Comma,              // ,
    /// <summary>The <c>;</c> token.</summary>
    Semicolon,          // ;
    /// <summary>The <c>.</c> token.</summary>
    Dot,                // .
    /// <summary>The <c>..  (range)</c> token.</summary>
    DotDot,             // ..  (range)
    /// <summary>The <c>:</c> token.</summary>
    Colon,              // :
    /// <summary>The <c>::  (type cast)</c> token.</summary>
    ColonColon,         // ::  (type cast)

    // ── Navigation & Graph Patterns ────────────────────────
    /// <summary>The <c>-&gt;</c> token.</summary>
    RightArrow,         // ->
    /// <summary>The <c>&lt;-</c> token.</summary>
    LeftArrow,          // <-

    // ── Special ────────────────────────────────────────────
    /// <summary>A named or numbered parameter prefixed by $ or @.</summary>
    Parameter,          // $1, $name, @param
    /// <summary>A line or block comment.</summary>
    Comment,            // -- line  or  /* block */

    // ── End of Input ───────────────────────────────────────
    /// <summary>The end of the source input.</summary>
    Eof,

    // ── Lexical Errors ─────────────────────────────────────
    // Appended after Eof so every existing member keeps its value.

    /// <summary>
    /// A character outside the shared lexer's fixed token classes, such as <c>?</c>, <c>#</c>,
    /// <c>^</c> or <c>§</c>. It is never automatically an identifier. A model parser may recognize
    /// its raw spelling as an operator (SQL <c>#</c>) or a deferred construct (SQL <c>^</c>);
    /// otherwise it reports its own syntax diagnostic at the character's span. A supplementary
    /// character (a surrogate pair) is one token.
    /// </summary>
    Unrecognized,
}
