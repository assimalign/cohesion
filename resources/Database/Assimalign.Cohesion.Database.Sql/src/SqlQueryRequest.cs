using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents a SQL query request: a parsed SQL statement plus optional named
/// parameter values (bound to <c>@name</c> / <c>$n</c> parameters in the statement).
/// </summary>
public sealed class SqlQueryRequest : QueryRequest<SqlQueryStatement>
{
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>
    /// Initializes a new <see cref="SqlQueryRequest"/> with the specified statement.
    /// </summary>
    /// <param name="statement">The parsed SQL statement.</param>
    public SqlQueryRequest(SqlQueryStatement statement)
        : base(statement)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="SqlQueryRequest"/> with the specified statement and parameters.
    /// </summary>
    /// <param name="statement">The parsed SQL statement.</param>
    /// <param name="parameters">The parameter values to bind, keyed by parameter name.</param>
    public SqlQueryRequest(SqlQueryStatement statement, IReadOnlyDictionary<string, object?> parameters)
        : base(statement)
    {
        _parameters = parameters;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?>? Parameters => _parameters;

    /// <summary>
    /// Parses SQL text into a request. Parse errors surface as
    /// <see cref="DatabaseException"/> — callers wanting diagnostics-level control
    /// parse with <see cref="SqlQueryParser"/> directly.
    /// </summary>
    /// <param name="sql">The SQL statement text.</param>
    /// <param name="parameters">The parameter values to bind, keyed by parameter name.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="DatabaseException">The text failed to parse.</exception>
    public static SqlQueryRequest FromSql(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var error = statement.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new DatabaseException($"SQL parse error {error.Code}: {error.Message}");
        }

        return parameters is null
            ? new SqlQueryRequest(statement)
            : new SqlQueryRequest(statement, parameters);
    }
}
