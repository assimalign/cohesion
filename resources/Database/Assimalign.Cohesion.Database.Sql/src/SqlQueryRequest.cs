using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
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
    /// Parses SQL text into a request, with the dialect's default expression nesting limit
    /// (<see cref="SqlQueryParserOptions.DefaultExpressionNestingLimit"/>). Parse errors surface as
    /// <see cref="DatabaseParseException"/> — callers wanting diagnostics-level
    /// control parse with <see cref="SqlQueryParser"/> directly, and callers targeting an engine
    /// configured with another nesting limit pass it through
    /// <see cref="FromSql(string, IReadOnlyDictionary{string, object?}?, SqlQueryParserOptions?)"/>.
    /// </summary>
    /// <param name="sql">The SQL statement text.</param>
    /// <param name="parameters">The parameter values to bind, keyed by parameter name.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="DatabaseParseException">The text failed to parse.</exception>
    /// <exception cref="DatabaseException">
    /// The text is within the nesting limit, but the calling thread has too little stack left to
    /// parse it: <c>COHSQLE004</c>, statement too complex (ISO SQLSTATE 54001), as for any other
    /// walk over a statement that runs out of stack (#1151).
    /// </exception>
    public static SqlQueryRequest FromSql(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => FromSql(sql, parameters, parserOptions: null);

    /// <summary>
    /// Parses SQL text into a request with the given parser options (#1151). Pass the expression
    /// nesting limit of the engine that will execute the request
    /// (<see cref="SqlDatabaseEngineOptions.ExpressionNestingLimit"/>), so the typed path accepts
    /// exactly what the engine accepts as text; the engine's own sessions parse text this way.
    /// Parse errors surface as <see cref="DatabaseParseException"/>, as for
    /// <see cref="FromSql(string, IReadOnlyDictionary{string, object?}?)"/>.
    /// </summary>
    /// <remarks>
    /// A limit above the engine's does not get a statement past it: the engine refuses a request
    /// that nests deeper than its own limit with <c>SQL0006</c>
    /// (<see cref="SqlQueryStatement.ExpressionNestingDepth"/>).
    /// </remarks>
    /// <param name="sql">The SQL statement text.</param>
    /// <param name="parameters">The parameter values to bind, keyed by parameter name.</param>
    /// <param name="parserOptions">The parser options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sql"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SqlQueryParserOptions.ExpressionNestingLimit"/> of <paramref name="parserOptions"/>
    /// is outside <see cref="SqlQueryParserOptions.MinimumExpressionNestingLimit"/>..<see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/>.
    /// </exception>
    /// <exception cref="DatabaseParseException">The text failed to parse.</exception>
    /// <exception cref="DatabaseException">
    /// The text is within the nesting limit, but the calling thread has too little stack left to
    /// parse it: <c>COHSQLE004</c>, statement too complex (ISO SQLSTATE 54001).
    /// </exception>
    public static SqlQueryRequest FromSql(string sql, IReadOnlyDictionary<string, object?>? parameters, SqlQueryParserOptions? parserOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var statement = (SqlQueryStatement)new SqlQueryParser(parserOptions).Parse(sql);

        var error = statement.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            if (error.Code == ParserOutOfStackCode)
            {
                // The text is within the dialect; the thread is too small for it. That is a
                // statement too complex for the executing thread, the same failure a planner or
                // evaluator walk out of stack reports, not a mistake in the text.
                throw SqlEvaluationException.StatementTooComplex(new InsufficientExecutionStackException(
                    $"SQL parse error {error.Code}: {error.Message}"));
            }

            throw new DatabaseParseException($"SQL parse error {error.Code}: {error.Message}");
        }

        return parameters is null
            ? new SqlQueryRequest(statement)
            : new SqlQueryRequest(statement, parameters);
    }

    // The parser's code for text within the nesting limit that the calling thread has too little
    // stack left to parse (#1151); the same text parses on a larger stack.
    private const string ParserOutOfStackCode = "SQL0007";

    /// <summary>
    /// Parser options at the highest nesting limit any engine can be configured with (#1151), for
    /// text the engine generates rather than receives: the definitions it persisted, and the
    /// statements of a schema migration. Those statements still meet the executing engine's own
    /// limit, which refuses a request nested deeper (<see cref="SqlQueryStatement.ExpressionNestingDepth"/>).
    /// </summary>
    internal static SqlQueryParserOptions CeilingParserOptions { get; } = new()
    {
        ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit,
    };
}
