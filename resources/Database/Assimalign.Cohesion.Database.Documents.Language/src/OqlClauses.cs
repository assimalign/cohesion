namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// The clause names an OQL
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> may enable.
/// </summary>
public static class OqlClauses
{
    /// <summary>Begins an explicit transaction; START is a synonym.</summary>
    public const string Begin = "BEGIN";

    /// <summary>Commits the session's explicit transaction.</summary>
    public const string Commit = "COMMIT";

    /// <summary>Rolls back the session's explicit transaction.</summary>
    public const string Rollback = "ROLLBACK";

    /// <summary>A <c>CREATE COLLECTION</c> statement.</summary>
    public const string CreateCollection = "CREATE COLLECTION";

    /// <summary>A <c>DROP COLLECTION</c> statement.</summary>
    public const string DropCollection = "DROP COLLECTION";

    /// <summary>A <c>CREATE INDEX</c> statement.</summary>
    public const string CreateIndex = "CREATE INDEX";

    /// <summary>A <c>DROP INDEX</c> statement.</summary>
    public const string DropIndex = "DROP INDEX";

    /// <summary>Inserts externally keyed documents.</summary>
    public const string Insert = "INSERT";

    /// <summary>Inserts or wholly replaces externally keyed documents.</summary>
    public const string Upsert = "UPSERT";

    /// <summary>Partially updates documents using SET and UNSET.</summary>
    public const string Update = "UPDATE";

    /// <summary>Removes named properties in an UPDATE.</summary>
    public const string Unset = "UNSET";

    /// <summary>Deletes documents from one collection.</summary>
    public const string Delete = "DELETE";

    /// <summary>Projects each affected document, optionally as one RAW or VALUE expression.</summary>
    public const string Returning = "RETURNING";

    /// <summary>Supplies key and value pairs to INSERT or UPSERT.</summary>
    public const string Values = "VALUES";

    /// <summary>Selects values from one or more document collections.</summary>
    public const string Select = "SELECT";

    /// <summary>Eliminates duplicate projected values.</summary>
    public const string Distinct = "DISTINCT";

    /// <summary>Projects one bare JSON value; RAW is a synonym.</summary>
    public const string SelectValue = "SELECT VALUE";

    /// <summary>Constructs a JSON object from named expressions.</summary>
    public const string ObjectConstructor = "OBJECT CONSTRUCTOR";

    /// <summary>Constructs a JSON array from expressions.</summary>
    public const string ArrayConstructor = "ARRAY CONSTRUCTOR";

    /// <summary>Maps qualifying elements into an array, omitting MISSING mappings.</summary>
    public const string ArrayComprehension = "ARRAY ... FOR";

    /// <summary>Returns the first qualifying array element's mapping.</summary>
    public const string FirstComprehension = "FIRST ... FOR";

    /// <summary>Describes a SELECT, INSERT, UPSERT, UPDATE or DELETE statement without executing it.</summary>
    public const string Explain = "EXPLAIN";

    /// <summary>Introduces the source collections and iteration variables for a query.</summary>
    public const string From = "FROM";

    /// <summary>Expands an array into inner rows.</summary>
    public const string Unnest = "UNNEST";

    /// <summary>Expands an array while preserving empty or non-array inputs.</summary>
    public const string LeftUnnest = "LEFT UNNEST";

    /// <summary>Restricts values by a predicate.</summary>
    public const string Where = "WHERE";

    /// <summary>Tests whether any array element satisfies a predicate; SOME is a synonym.</summary>
    public const string Any = "ANY ... SATISFIES";

    /// <summary>Tests whether every array element satisfies a predicate.</summary>
    public const string Every = "EVERY ... SATISFIES";

    /// <summary>Tests whether an array is nonempty, preserving MISSING.</summary>
    public const string Exists = "EXISTS";

    /// <summary>Tests structural membership in an explicit value list or array value.</summary>
    public const string In = "IN";

    /// <summary>Tests an inclusive scalar range.</summary>
    public const string Between = "BETWEEN";

    /// <summary>Matches a string pattern with optional ESCAPE.</summary>
    public const string Like = "LIKE";

    /// <summary>Evaluates searched or simple conditional branches.</summary>
    public const string Case = "CASE";

    /// <summary>Groups values by one or more expressions.</summary>
    public const string GroupBy = "GROUP BY";

    /// <summary>Retains each group's input binding wrappers as an array.</summary>
    public const string GroupAs = "GROUP AS";

    /// <summary>Restricts grouped values by a predicate.</summary>
    public const string Having = "HAVING";

    /// <summary>Orders values by one or more expressions.</summary>
    public const string OrderBy = "ORDER BY";

    /// <summary>Limits the number of query result rows.</summary>
    public const string Limit = "LIMIT";

    /// <summary>Skips query result rows before applying LIMIT.</summary>
    public const string Offset = "OFFSET";

    /// <summary>Evaluates a query nested within another OQL expression.</summary>
    public const string Subquery = "SUBQUERY";
}
