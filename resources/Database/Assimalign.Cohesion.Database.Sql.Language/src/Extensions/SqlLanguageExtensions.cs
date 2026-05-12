using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

public static class SqlLanguageExtensions
{
    private static ReadOnlySpan<string> _keywords => new ReadOnlySpan<string>([
        // DML
        "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE",
        "SET", "VALUES", "INTO", "RETURNING",
        // DDL
        "CREATE", "DROP", "ALTER", "TABLE", "INDEX", "VIEW", "IF",
        "ADD", "COLUMN",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES",
        "CONSTRAINT", "DEFAULT", "CHECK", "UNIQUE",
        "CASCADE", "RESTRICT",
        // Joins
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER",
        "CROSS", "FULL", "NATURAL", "ON", "USING",
        // Clauses & Modifiers
        "AS", "GROUP", "BY", "ORDER", "ASC", "DESC",
        "HAVING", "LIMIT", "OFFSET", "FETCH", "NEXT",
        "ONLY", "ROWS", "DISTINCT", "ALL", "TOP",
        // Set Operations
        "UNION", "INTERSECT", "EXCEPT",
        // Logical
        "AND", "OR", "NOT",
        // Predicates
        "IN", "EXISTS", "BETWEEN", "LIKE", "IS",
        // Literals / Constants
        "NULL", "TRUE", "FALSE",
        // CASE
        "CASE", "WHEN", "THEN", "ELSE", "END",
        // CTE & Transactions
        "WITH", "RECURSIVE",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
        // Window
        "OVER", "PARTITION", "WINDOW", "RANGE",
        "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "UNBOUNDED",
    ]);

    private static ReadOnlySpan<string> _functions => new ReadOnlySpan<string>([
        // Aggregate
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // Null Handling
        "COALESCE", "NULLIF",
        // Type
        "CAST",
        // String
        "TRIM", "LTRIM", "RTRIM", "UPPER", "LOWER",
        "SUBSTRING", "LENGTH", "REPLACE", "CONCAT",
        // Numeric
        "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "MOD",
        // Date / Time
        "NOW", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT",
        // Window
        "ROW_NUMBER", "RANK", "DENSE_RANK",
        "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE",
    ]);




    extension(TokenLexerOptions options)
    {
        public static TokenLexerOptions Sql => new TokenLexerOptions()
        {
            Keywords = _keywords,
            Functions = _functions,
            IsCaseSensitive = false
        };
    }
}
