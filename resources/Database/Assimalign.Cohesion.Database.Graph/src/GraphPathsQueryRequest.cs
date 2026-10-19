using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A database-scoped MATCH request that projects one path or bound graph entity.</summary>
/// <remarks>
/// Execute through the graph session's typed member,
/// <see cref="DatabaseExecutionSession{TRequest}.ExecuteAsync(TRequest, System.Threading.CancellationToken)"/>
/// on a <see cref="GraphDatabaseSession"/>, whose request root is <see cref="GraphRequest"/>.
/// A named path retains traversal order; a node becomes a one-node path, and a relationship becomes
/// a path containing its source and target nodes in stored direction. DISTINCT compares graph identity;
/// RETURN * is accepted when exactly one named binding is in scope. Computed and multiple projections,
/// catalog statements, mutations, and variables bound only by optional clauses are rejected while planning.
/// </remarks>
public sealed class GraphPathsQueryRequest : GraphRequest
{
    /// <summary>Creates a path request from a parsed GQL statement.</summary>
    /// <param name="statement">The parsed MATCH statement.</param>
    /// <param name="parameters">Named scalar, list or map bindings for $name and @name; see <see cref="GraphRequest.Parameters"/>.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public GraphPathsQueryRequest(GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(statement, parameters, paths: true)
    {
    }

    /// <summary>Parses a MATCH statement for path execution.</summary>
    /// <param name="gql">The GQL statement text.</param>
    /// <param name="parameters">Named scalar, list or map bindings for $name and @name; see <see cref="GraphRequest.Parameters"/>.</param>
    /// <returns>The parsed path request.</returns>
    /// <exception cref="ArgumentException">The statement text is empty.</exception>
    /// <exception cref="DatabaseParseException">The statement has an error diagnostic.</exception>
    /// <exception cref="DatabaseException">
    /// The text nests deeper than the calling thread's stack lets the parser follow:
    /// <c>COHDBG008</c>, statement too complex.
    /// </exception>
    public static GraphPathsQueryRequest FromGql(string gql, IReadOnlyDictionary<string, object?>? parameters = null)
        => new(GraphQueryRequest.FromGql(gql, parameters).Statement, parameters);
}
