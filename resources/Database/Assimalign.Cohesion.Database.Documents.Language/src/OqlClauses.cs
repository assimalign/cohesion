namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// The clause names an OQL
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> may enable.
/// </summary>
public static class OqlClauses
{
    /// <summary>A <c>CREATE INDEX</c> statement.</summary>
    public const string CreateIndex = "CREATE INDEX";

    /// <summary>A <c>DROP INDEX</c> statement.</summary>
    public const string DropIndex = "DROP INDEX";

    /// <summary>Selects values from one or more document collections.</summary>
    public const string Select = "SELECT";

    /// <summary>Introduces the source collections and iteration variables for a query.</summary>
    public const string From = "FROM";

    /// <summary>Restricts values by a predicate.</summary>
    public const string Where = "WHERE";

    /// <summary>Groups values by one or more expressions.</summary>
    public const string GroupBy = "GROUP BY";

    /// <summary>Restricts grouped values by a predicate.</summary>
    public const string Having = "HAVING";

    /// <summary>Orders values by one or more expressions.</summary>
    public const string OrderBy = "ORDER BY";

    /// <summary>Defines a named OQL query.</summary>
    public const string Define = "DEFINE";

    /// <summary>Extracts the sole value from a singleton collection.</summary>
    public const string Element = "ELEMENT";

    /// <summary>Flattens a nested collection by one level.</summary>
    public const string Flatten = "FLATTEN";

    /// <summary>Evaluates a query nested within another OQL expression.</summary>
    public const string Subquery = "SUBQUERY";
}
