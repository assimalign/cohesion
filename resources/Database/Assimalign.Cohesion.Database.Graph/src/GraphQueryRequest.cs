using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A parsed, database-scoped GQL statement whose result is rows.</summary>
/// <remarks>
/// Node and relationship property maps and WHERE operands bind named scalar parameters. The
/// statement and the parameters live on <see cref="GraphRequest"/>, which a graph session's typed
/// execute member takes.
/// </remarks>
public sealed class GraphQueryRequest : GraphRequest
{
    /// <summary>Creates a request from a parsed GQL statement.</summary>
    /// <param name="statement">The parsed statement.</param>
    /// <param name="parameters">Named scalar bindings for $name and @name; see <see cref="GraphRequest.Parameters"/>.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public GraphQueryRequest(GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(statement, parameters)
    {
    }

    /// <summary>Parses GQL text into an executable graph statement request.</summary>
    /// <param name="gql">The GQL statement text.</param>
    /// <param name="parameters">Named scalar bindings for $name and @name; see <see cref="GraphRequest.Parameters"/>.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="ArgumentException">The statement text is empty.</exception>
    /// <exception cref="DatabaseParseException">The statement has an error diagnostic.</exception>
    /// <exception cref="DatabaseException">
    /// The text nests deeper than the calling thread's stack lets the parser follow:
    /// <c>COHDBG008</c>, statement too complex. No length or nesting limit applies to label
    /// expressions or predicates, so the same text parses on a thread with more stack.
    /// </exception>
    public static GraphQueryRequest FromGql(string gql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gql);
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            // The text is within the language; the thread is too small for it. That is the
            // statement-too-complex failure a planner or evaluator walk out of stack reports.
            if (error.Code == GraphStatementTooComplex.ParserCode) { throw GraphStatementTooComplex.FromParse(error); }
            throw new DatabaseParseException($"GQL parse error {error.Code}: {error.Message}", statement.Diagnostics);
        }
        return new GraphQueryRequest(statement, parameters);
    }
}
