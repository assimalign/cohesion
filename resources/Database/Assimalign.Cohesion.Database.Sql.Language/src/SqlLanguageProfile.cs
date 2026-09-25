namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Exposes the lexical vocabulary and supported clause surface of the SQL model.
/// </summary>
public static class SqlLanguageProfile
{
    private static readonly string[] _keywords =
    [
        // DML
        "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE",
        "SET", "VALUES", "INTO", "RETURNING",
        // DDL
        "CREATE", "DROP", "ALTER", "TABLE", "INDEX", "VIEW", "IF",
        "ADD", "COLUMN",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES",
        "CONSTRAINT", "DEFAULT", "CHECK", "UNIQUE", "COLLATE",
        "CASCADE", "RESTRICT",
        // Joins
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER",
        "CROSS", "FULL", "NATURAL", "ON", "USING",
        // Clauses and modifiers
        "AS", "GROUP", "BY", "ORDER", "ASC", "DESC",
        "HAVING", "LIMIT", "OFFSET", "FETCH", "NEXT",
        "ONLY", "ROWS", "DISTINCT", "ALL", "TOP",
        // Set operations
        "UNION", "INTERSECT", "EXCEPT",
        // Logical
        "AND", "OR", "NOT",
        // Predicates
        "IN", "EXISTS", "BETWEEN", "LIKE", "IS",
        // Literals and constants
        "NULL", "TRUE", "FALSE",
        // CASE
        "CASE", "WHEN", "THEN", "ELSE", "END",
        // CTEs and transactions
        "WITH", "RECURSIVE",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
        // Windows
        "OVER", "PARTITION", "WINDOW", "RANGE",
        "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "UNBOUNDED",
    ];

    private static readonly string[] _functions =
    [
        // Aggregate
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // Null handling
        "COALESCE", "NULLIF",
        // Type
        "CAST",
        // String
        "TRIM", "LTRIM", "RTRIM", "UPPER", "LOWER",
        "SUBSTRING", "LENGTH", "REPLACE", "CONCAT",
        // Numeric
        "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "MOD",
        // Date and time
        "NOW", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT",
        // Window
        "ROW_NUMBER", "RANK", "DENSE_RANK",
        "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE",
    ];

    private static readonly string[] _clauses =
    [
        SqlClauses.Select,
        SqlClauses.Insert,
        SqlClauses.Update,
        SqlClauses.Delete,
        SqlClauses.CreateTable,
        SqlClauses.CreateIndex,
        SqlClauses.AlterTable,
        SqlClauses.DropTable,
        SqlClauses.DropIndex,
        SqlClauses.From,
        SqlClauses.Join,
        SqlClauses.Where,
        SqlClauses.GroupBy,
        SqlClauses.Having,
        SqlClauses.OrderBy,
        SqlClauses.Limit,
        SqlClauses.Offset,
        SqlClauses.Values,
        SqlClauses.Subquery,
        SqlClauses.Case,
        SqlClauses.Cast,
        SqlClauses.Collate,
        SqlClauses.Constraint,
        SqlClauses.ForeignKey,
        SqlClauses.References,
        SqlClauses.Check,
        SqlClauses.UniqueConstraint,
        SqlClauses.Cascade,
        SqlClauses.Restrict,
        SqlClauses.Begin,
        SqlClauses.Commit,
        SqlClauses.Rollback,
        SqlClauses.Transaction,
    ];

    /// <summary>
    /// Gets the SQL language profile. Clauses awaiting execution support are excluded even
    /// when <see cref="SqlQueryParser"/> can construct their syntax trees.
    /// </summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "SQL",
        _keywords,
        _functions,
        _clauses);

    /// <summary>
    /// Reports whether a join type executes in the SQL surface. Supported joins
    /// require an ON predicate and are limited to two tables per SELECT.
    /// </summary>
    /// <param name="joinType">The parsed join type.</param>
    /// <returns><see langword="true"/> only for INNER JOIN (including bare JOIN).</returns>
    public static bool SupportsJoin(SqlJoinType joinType) => joinType == SqlJoinType.Inner;
}
