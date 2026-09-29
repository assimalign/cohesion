using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A parsed, database-scoped GQL statement.</summary>
/// <remarks>The current executable subset uses literals; parameter expressions report COHDBL001.</remarks>
public sealed class GraphQueryRequest : QueryRequest<GqlQueryStatement>
{
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>Creates a request from a parsed GQL statement.</summary>
    /// <param name="statement">The parsed statement.</param>
    /// <param name="parameters">Reserved parameter values; the current subset has no parameter expressions.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public GraphQueryRequest(GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(statement ?? throw new ArgumentNullException(nameof(statement)))
    {
        _parameters = parameters;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?>? Parameters => _parameters;

    /// <summary>Parses GQL text into an executable graph statement request.</summary>
    /// <param name="gql">The GQL statement text.</param>
    /// <param name="parameters">Reserved parameter values; the current subset has no parameter expressions.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="ArgumentException">The statement text is empty.</exception>
    /// <exception cref="DatabaseParseException">The statement has an error diagnostic.</exception>
    public static GraphQueryRequest FromGql(string gql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gql);
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new DatabaseParseException($"GQL parse error {error.Code}: {error.Message}");
        }
        return new GraphQueryRequest(statement, parameters);
    }
}
