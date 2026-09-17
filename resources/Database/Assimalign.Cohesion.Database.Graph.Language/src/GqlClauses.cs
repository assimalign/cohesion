namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// The clause names a GQL
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> may enable.
/// </summary>
public static class GqlClauses
{
    /// <summary>Matches a graph pattern.</summary>
    public const string Match = "MATCH";

    /// <summary>Matches a graph pattern without requiring a result.</summary>
    public const string OptionalMatch = "OPTIONAL MATCH";

    /// <summary>Matches a graph pattern and requires a result.</summary>
    public const string MandatoryMatch = "MANDATORY MATCH";

    /// <summary>Projects values from a graph query.</summary>
    public const string Return = "RETURN";

    /// <summary>Passes projected values to a following query stage.</summary>
    public const string With = "WITH";

    /// <summary>Creates graph elements from a pattern.</summary>
    public const string Create = "CREATE";

    /// <summary>Inserts graph elements.</summary>
    public const string Insert = "INSERT";

    /// <summary>Deletes graph elements.</summary>
    public const string Delete = "DELETE";

    /// <summary>Deletes graph elements together with attached relationships.</summary>
    public const string DetachDelete = "DETACH DELETE";

    /// <summary>Sets graph element properties or labels.</summary>
    public const string Set = "SET";

    /// <summary>Removes graph element properties or labels.</summary>
    public const string Remove = "REMOVE";

    /// <summary>Matches or creates graph elements from a pattern.</summary>
    public const string Merge = "MERGE";

    /// <summary>Restricts graph results by a predicate.</summary>
    public const string Where = "WHERE";

    /// <summary>Orders graph results by one or more expressions.</summary>
    public const string OrderBy = "ORDER BY";

    /// <summary>Limits the number of graph results.</summary>
    public const string Limit = "LIMIT";

    /// <summary>Offsets the first graph result returned.</summary>
    public const string Offset = "OFFSET";

    /// <summary>Skips a number of graph results.</summary>
    public const string Skip = "SKIP";

    /// <summary>Binds a value for use by later query stages.</summary>
    public const string Let = "LET";

    /// <summary>Expands a collection into individual query rows.</summary>
    public const string Unwind = "UNWIND";

    /// <summary>Applies an update for each value in a collection.</summary>
    public const string Foreach = "FOREACH";

    /// <summary>Combines graph query results with a set operation.</summary>
    public const string SetOperation = "UNION";

    /// <summary>Evaluates conditional branches in an expression.</summary>
    public const string Case = "CASE";

    /// <summary>Invokes a graph query procedure.</summary>
    public const string Call = "CALL";

    /// <summary>Projects values produced by a procedure.</summary>
    public const string Yield = "YIELD";

    /// <summary>Filters values in a graph query stage.</summary>
    public const string Filter = "FILTER";

    /// <summary>Finds a shortest path through a graph pattern.</summary>
    public const string ShortestPath = "SHORTEST PATH";
}
