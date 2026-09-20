using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A database-scoped MATCH request that projects one path or bound graph entity.</summary>
/// <remarks>
/// Execute through <see cref="IDatabaseSession.ExecuteAsync(QueryRequest, System.Threading.CancellationToken)"/>.
/// A named path retains traversal order; a node becomes a one-node path, and a relationship becomes
/// a path containing its source and target nodes in stored direction. Scalar and multiple projections,
/// catalog statements, and mutations are rejected before execution.
/// </remarks>
public sealed class GraphPathsQueryRequest : QueryRequest<GqlQueryStatement>
{
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>Creates a path request from a parsed GQL statement.</summary>
    /// <param name="statement">The parsed MATCH statement.</param>
    /// <param name="parameters">Reserved parameter values; the current subset has no parameter expressions.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public GraphPathsQueryRequest(GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(statement ?? throw new ArgumentNullException(nameof(statement)))
    {
        _parameters = parameters;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?>? Parameters => _parameters;

    /// <summary>Parses a MATCH statement for path execution.</summary>
    /// <param name="gql">The GQL statement text.</param>
    /// <param name="parameters">Reserved parameter values; the current subset has no parameter expressions.</param>
    /// <returns>The parsed path request.</returns>
    /// <exception cref="ArgumentException">The statement text is empty.</exception>
    /// <exception cref="DatabaseParseException">The statement has an error diagnostic.</exception>
    public static GraphPathsQueryRequest FromGql(string gql, IReadOnlyDictionary<string, object?>? parameters = null)
        => new(GraphQueryRequest.FromGql(gql, parameters).Statement, parameters);
}
