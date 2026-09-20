using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// SQL-dialect extensions for the shared lexer: the declared keyword and builtin
/// function tables. The authoritative statement and function support matrix lives
/// in this project's <c>docs/DIALECT.md</c> — tokens listed here may be recognized
/// by the lexer ahead of parser support so diagnostics stay precise.
/// </summary>
public static class SqlLanguageExtensions
{
    extension(TokenLexerOptions options)
    {
        /// <summary>
        /// Gets the lexer options for the declared SQL dialect (case-insensitive
        /// keywords and builtin function names).
        /// </summary>
        public static TokenLexerOptions Sql => SqlLanguageProfile.Instance.ToLexerOptions();
    }
}
