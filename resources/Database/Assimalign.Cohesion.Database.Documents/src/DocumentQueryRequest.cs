using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A parsed, database-scoped OQL query and its named parameter values.</summary>
public sealed class DocumentQueryRequest : QueryRequest<OqlQueryStatement>
{
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>Creates a request from a parsed OQL statement.</summary>
    /// <param name="statement">The parsed statement.</param>
    /// <param name="parameters">Named parameter values, without their @ or $ prefix.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public DocumentQueryRequest(OqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(statement ?? throw new ArgumentNullException(nameof(statement)))
    {
        _parameters = parameters;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?>? Parameters => _parameters;

    /// <summary>Parses OQL text into an executable document query request.</summary>
    /// <param name="oql">The OQL query text.</param>
    /// <param name="parameters">Named parameter values, without their @ or $ prefix.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="ArgumentException">The query text is empty.</exception>
    /// <exception cref="DatabaseParseException">The query has an error diagnostic.</exception>
    public static DocumentQueryRequest FromOql(string oql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oql);
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(oql);
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new DatabaseParseException($"OQL parse error {error.Code}: {error.Message}");
        }
        return new DocumentQueryRequest(statement, parameters);
    }
}
