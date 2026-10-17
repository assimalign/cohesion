namespace Assimalign.Cohesion.Database.Sql.Language;

using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>
/// Exposes the SQL lexical vocabulary, executable built-in functions and supported clauses.
/// </summary>
public static class SqlLanguageProfile
{
    private static readonly string[] _keywords =
    [
        // DML
        "SELECT", "EXPLAIN", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE",
        "SET", "VALUES", "INTO", "RETURNING",
        "FUNCTION", "DOMAIN", "RETURNS", "RETURN", "IMMUTABLE", "STABLE", "VOLATILE", "STRICT", "CALLED", "INPUT",
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
        "COALESCE",
        // Type
        "CAST",
        // String
        "UPPER", "LOWER", "LENGTH",
        // Numeric
        "ABS",
    ];

    private static readonly string[] _clauses =
    [
        SqlClauses.Select,
        SqlClauses.Explain,
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
        SqlClauses.Using,
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
    /// Gets the recognized but unsupported SQL spellings. Renderers quote identifiers matching
    /// these words; executable functions are listed separately by <see cref="Instance"/>.
    /// </summary>
    public static IReadOnlyList<string> RecognizedUnsupportedVocabulary { get; } =
        Array.AsReadOnly(SqlUnsupportedVocabulary.Words.Select(word => word.Spelling).ToArray());

    /// <summary>
    /// Reports whether a join type executes in the SQL surface. Supported joins
    /// run left-deep in written order. INNER and LEFT require ON or USING; CROSS has neither.
    /// </summary>
    /// <param name="joinType">The parsed join type.</param>
    /// <returns><see langword="true"/> for INNER (including bare JOIN), LEFT OUTER and CROSS JOIN.</returns>
    public static bool SupportsJoin(SqlJoinType joinType) => joinType is SqlJoinType.Inner or SqlJoinType.LeftOuter or SqlJoinType.Cross;
}
