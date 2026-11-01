namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// The clause names a GQL
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> may enable.
/// </summary>
public static class GqlClauses
{
    /// <summary>The Cohesion database-scoped catalog introspection extension.</summary>
    public const string Show = "SHOW";

    /// <summary>Create a named undo scope.</summary>
    public const string Savepoint = "SAVEPOINT";
    /// <summary>Release a named undo scope and descendants.</summary>
    public const string ReleaseSavepoint = "RELEASE SAVEPOINT";
    /// <summary>Undo to a named scope, retaining it.</summary>
    public const string RollbackToSavepoint = "ROLLBACK TO SAVEPOINT";
    /// <summary>Standalone create index schema DDL.</summary>
    public const string CreateIndex = "CREATE INDEX";

    /// <summary>Standalone drop index schema DDL.</summary>
    public const string DropIndex = "DROP INDEX";

    /// <summary>Standalone create constraint schema DDL.</summary>
    public const string CreateConstraint = "CREATE CONSTRAINT";

    /// <summary>Standalone drop constraint schema DDL.</summary>
    public const string DropConstraint = "DROP CONSTRAINT";

    /// <summary>Standalone create label schema DDL.</summary>
    public const string CreateLabel = "CREATE LABEL";

    /// <summary>Standalone drop label schema DDL.</summary>
    public const string DropLabel = "DROP LABEL";

    /// <summary>Standalone create relationship type schema DDL.</summary>
    public const string CreateRelationshipType = "CREATE RELATIONSHIP TYPE";

    /// <summary>Standalone drop relationship type schema DDL.</summary>
    public const string DropRelationshipType = "DROP RELATIONSHIP TYPE";

    /// <summary>Matches a graph pattern.</summary>
    public const string Match = "MATCH";

    /// <summary>Matches quantified relationships and parenthesized paths under the outer path mode.</summary>
    public const string QuantifiedPath = "QUANTIFIED PATH";

    /// <summary>Applies WALK, TRAIL, SIMPLE or ACYCLIC identity rules to a complete matched path.</summary>
    public const string PathMode = "PATH MODE";

    /// <summary>Selects ANY, ALL, shortest paths or shortest length groups per endpoint pair.</summary>
    public const string PathSelector = "PATH SELECTOR";

    /// <summary>The Cohesion extension that describes a data statement without executing it.</summary>
    public const string Explain = "EXPLAIN";

    /// <summary>Matches a graph pattern without requiring a result.</summary>
    public const string OptionalMatch = "OPTIONAL MATCH";

    /// <summary>Matches a graph pattern and requires a result.</summary>
    public const string MandatoryMatch = "MANDATORY MATCH";

    /// <summary>Projects values from a graph query.</summary>
    public const string Return = "RETURN";

    /// <summary>Starts a following linear query segment.</summary>
    public const string Next = "NEXT";

    /// <summary>Groups by exactly the non-aggregated RETURN expressions.</summary>
    public const string GroupBy = "GROUP BY";

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

    /// <summary>
    /// ISO/IEC 39075 label expressions (<c>|</c>, <c>&amp;</c>, <c>!</c>, <c>%</c>, parentheses and
    /// <c>IS</c>) in node and edge patterns, and the labeled predicate
    /// (<c>n IS [NOT] LABELED A</c>, <c>n:A</c>) in <c>WHERE</c>.
    /// </summary>
    public const string LabelExpression = "LABEL EXPRESSION";

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

    /// <summary>Expands a list into one row for each element.</summary>
    public const string For = "FOR";

    /// <summary>Expands a collection into individual query rows.</summary>
    public const string Unwind = "UNWIND";

    /// <summary>Applies an update for each value in a collection.</summary>
    public const string Foreach = "FOREACH";

    /// <summary>Combines graph query results with a set operation.</summary>
    public const string SetOperation = "UNION";

    /// <summary>Combines read-only query results with structural duplicate elimination.</summary>
    public const string UnionDistinct = "UNION DISTINCT";

    /// <summary>Concatenates read-only query results with duplicate rows preserved.</summary>
    public const string UnionAll = "UNION ALL";

    /// <summary>Returns distinct rows present only in the left result.</summary>
    public const string Except = "EXCEPT";

    /// <summary>Explicitly selects distinct difference.</summary>
    public const string ExceptDistinct = "EXCEPT DISTINCT";

    /// <summary>Subtracts right multiplicities from left multiplicities.</summary>
    public const string ExceptAll = "EXCEPT ALL";

    /// <summary>Returns distinct rows shared by both results.</summary>
    public const string Intersect = "INTERSECT";

    /// <summary>Explicitly selects distinct intersection.</summary>
    public const string IntersectDistinct = "INTERSECT DISTINCT";

    /// <summary>Returns the smaller multiplicity for each shared row.</summary>
    public const string IntersectAll = "INTERSECT ALL";

    /// <summary>Evaluates the right query only when the whole left result is empty.</summary>
    public const string Otherwise = "OTHERWISE";

    /// <summary>Tests whether a correlated inner graph pattern produces any row.</summary>
    public const string Exists = "EXISTS";

    /// <summary>Evaluates conditional branches in an expression.</summary>
    public const string Case = "CASE";

    /// <summary>A list comprehension with an iterator, optional filter and optional projection.</summary>
    public const string ListComprehension = "LIST COMPREHENSION";

    /// <summary>Correlated pattern collection expressions.</summary>
    public const string PatternComprehension = "PATTERN COMPREHENSION";

    /// <summary>A list slice with optional inclusive start and exclusive end.</summary>
    public const string ListSlice = "LIST SLICING";

    /// <summary>Ordered reduction with lexical accumulator and iterator bindings.</summary>
    public const string Reduce = "REDUCE";

    /// <summary>Three-valued ALL, ANY, NONE and SINGLE predicates over lists.</summary>
    public const string ListPredicate = "LIST PREDICATE";

    /// <summary>Invokes a graph query procedure.</summary>
    public const string Call = "CALL";

    /// <summary>Projects values produced by a procedure.</summary>
    public const string Yield = "YIELD";

    /// <summary>Filters values in a graph query stage.</summary>
    public const string Filter = "FILTER";

    /// <summary>Finds a shortest path through a graph pattern.</summary>
    public const string ShortestPath = "SHORTEST PATH";
}
