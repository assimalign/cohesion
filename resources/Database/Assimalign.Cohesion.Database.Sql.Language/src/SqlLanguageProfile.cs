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
        "SELECT", "EXPLAIN", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "MERGE",
        "SET", "VALUES", "INTO", "RETURNING",
        "FUNCTION", "DOMAIN", "RETURNS", "RETURN", "IMMUTABLE", "STABLE", "VOLATILE", "STRICT", "CALLED", "INPUT",
        // DDL
        "CREATE", "DROP", "TRUNCATE", "ALTER", "TABLE", "INDEX", "SEQUENCE", "VIEW", "IF",
        "ADD", "COLUMN", "SERIAL", "BIGSERIAL", "SMALLSERIAL", "OVERRIDING", "MINVALUE", "MAXVALUE", "CYCLE", "CACHE", "RESTART",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES",
        "CONSTRAINT", "DEFAULT", "CHECK", "UNIQUE", "COLLATE",
        "CASCADE", "RESTRICT",
        // Joins
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER",
        "CROSS", "FULL", "NATURAL", "ON", "USING",
        // Clauses and modifiers
        "AS", "GROUP", "BY", "ORDER", "ASC", "DESC",
        "HAVING", "FILTER", "LIMIT", "OFFSET", "FETCH", "NEXT",
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
        "COUNT", "SUM", "AVG", "MIN", "MAX", "STRING_AGG", "LISTAGG",
        "BOOL_AND", "BOOL_OR", "EVERY", "STDDEV", "STDDEV_SAMP", "STDDEV_POP", "VARIANCE", "VAR_SAMP", "VAR_POP",
        "GROUPING", "GROUPING_ID",
        // Window functions
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE", "LAG", "LEAD",
        // Null handling
        "COALESCE", "NULLIF",
        // Type
        "CAST",
        // String
        "UPPER", "LOWER", "LENGTH", "CHAR_LENGTH", "CHARACTER_LENGTH",
        "TRIM", "LTRIM", "RTRIM", "SUBSTRING", "REPLACE", "CONCAT", "CONCAT_WS",
        "POSITION", "STRPOS", "LEFT", "RIGHT", "LPAD", "RPAD", "REVERSE",
        // Numeric
        "ABS", "ROUND", "FLOOR", "CEILING", "CEIL", "POWER", "SQRT", "MOD", "SIGN", "EXP", "LN", "LOG10",
        // Temporal
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP", "NOW", "EXTRACT", "DATE_TRUNC",
        // Sequences
        "NEXTVAL", "CURRVAL", "SETVAL", "LASTVAL",
        // UUID
        "GEN_RANDOM_UUID",
    ];

    private static readonly string[] _clauses =
    [
        SqlClauses.Select,
        SqlClauses.Explain,
        SqlClauses.Insert,
        SqlClauses.OnConflict,
        SqlClauses.Merge,
        SqlClauses.Update,
        SqlClauses.UpdateFrom,
        SqlClauses.Delete,
        SqlClauses.DeleteUsing,
        SqlClauses.Returning,
        SqlClauses.CreateTable,
        SqlClauses.CreateTableAs,
        SqlClauses.CreateSchema,
        SqlClauses.Identity,
        SqlClauses.AlterIdentity,
        SqlClauses.Serial,
        SqlClauses.Overriding,
        SqlClauses.OwnedBy,
        SqlClauses.CreateSequence,
        SqlClauses.AlterSequence,
        SqlClauses.DropSequence,
        SqlClauses.NextValue,
        SqlClauses.DiscardAll,
        SqlClauses.CreateIndex,
        SqlClauses.AlterTable,
        SqlClauses.DropTable,
        SqlClauses.Truncate,
        SqlClauses.CreateView,
        SqlClauses.DropView,
        SqlClauses.DropSchema,
        SqlClauses.DropIndex,
        SqlClauses.From,
        SqlClauses.Join,
        SqlClauses.Using,
        SqlClauses.Where,
        SqlClauses.GroupBy,
        SqlClauses.GroupingSets,
        SqlClauses.Rollup,
        SqlClauses.Cube,
        SqlClauses.Filter,
        SqlClauses.Having,
        SqlClauses.Over,
        SqlClauses.Partition,
        SqlClauses.Window,
        SqlClauses.OrderBy,
        SqlClauses.Limit,
        SqlClauses.Offset,
        SqlClauses.Fetch,
        SqlClauses.Top,
        SqlClauses.Values,
        SqlClauses.SetOperation,
        SqlClauses.Intersect,
        SqlClauses.Except,
        SqlClauses.Subquery,
        SqlClauses.QuantifiedComparison,
        SqlClauses.Cte,
        SqlClauses.Recursive,
        SqlClauses.Case,
        SqlClauses.Cast,
        SqlClauses.Collate,
        SqlClauses.Constraint,
        SqlClauses.ForeignKey,
        SqlClauses.References,
        SqlClauses.OnUpdate,
        SqlClauses.Check,
        SqlClauses.UniqueConstraint,
        SqlClauses.Cascade,
        SqlClauses.Restrict,
        SqlClauses.SetNull,
        SqlClauses.SetDefault,
        SqlClauses.NoAction,
        SqlClauses.Begin,
        SqlClauses.Commit,
        SqlClauses.Rollback,
        SqlClauses.Transaction,
    ];

    /// <summary>
    /// Gets the SQL language profile, including MERGE actions, BY SOURCE, RETURNING,
    /// repeated-target detection and EXPLAIN. WITH RECURSIVE binds; recursive execution is deferred.
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
    /// associate in written order. INNER, LEFT, RIGHT and FULL require ON or USING; CROSS has neither.
    /// </summary>
    /// <param name="joinType">The parsed join type.</param>
    /// <returns><see langword="true"/> for INNER (including bare JOIN), LEFT OUTER, RIGHT OUTER, FULL OUTER and CROSS JOIN.</returns>
    public static bool SupportsJoin(SqlJoinType joinType) => joinType is SqlJoinType.Inner or SqlJoinType.LeftOuter or SqlJoinType.RightOuter or SqlJoinType.FullOuter or SqlJoinType.Cross;
}
