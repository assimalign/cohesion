namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// The clause names an OQL
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> may enable.
/// </summary>
public static class OqlClauses
{
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

    /// <summary>Supplies key and value pairs to INSERT.</summary>
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

    /// <summary>Describes a SELECT or INSERT statement without executing it.</summary>
    public const string Explain = "EXPLAIN";

    /// <summary>Introduces the source collections and iteration variables for a query.</summary>
    public const string From = "FROM";

    /// <summary>Restricts values by a predicate.</summary>
    public const string Where = "WHERE";

    /// <summary>Tests membership in an explicit value list.</summary>
    public const string In = "IN";

    /// <summary>Tests an inclusive scalar range.</summary>
    public const string Between = "BETWEEN";

    /// <summary>Matches a string pattern with optional ESCAPE.</summary>
    public const string Like = "LIKE";

    /// <summary>Evaluates searched or simple conditional branches.</summary>
    public const string Case = "CASE";

    /// <summary>Groups values by one or more expressions.</summary>
    public const string GroupBy = "GROUP BY";

    /// <summary>Restricts grouped values by a predicate.</summary>
    public const string Having = "HAVING";

    /// <summary>Orders values by one or more expressions.</summary>
    public const string OrderBy = "ORDER BY";

    /// <summary>Evaluates a query nested within another OQL expression.</summary>
    public const string Subquery = "SUBQUERY";
}
