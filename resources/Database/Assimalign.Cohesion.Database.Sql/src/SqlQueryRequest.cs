using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents a SQL query request that wraps a parsed SQL statement.
/// </summary>
/// <remarks>
/// Parameters are used as a bridge for DML operations until the T-SQL-like
/// scripting language produces a full AST. Current parameter conventions:
/// <list type="bullet">
///   <item><b>INSERT:</b> <c>Parameters["row"]</c> as <c>byte[]</c></item>
///   <item><b>UPDATE:</b> <c>Parameters["pageId"]</c>, <c>Parameters["slotIndex"]</c>, <c>Parameters["row"]</c></item>
///   <item><b>DELETE:</b> <c>Parameters["pageId"]</c>, <c>Parameters["slotIndex"]</c></item>
///   <item><b>SELECT:</b> No parameters required (full table scan)</item>
/// </list>
/// </remarks>
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
    /// <param name="parameters">The parameters to bind to the query.</param>
    public SqlQueryRequest(SqlQueryStatement statement, IReadOnlyDictionary<string, object?> parameters)
        : base(statement)
    {
        _parameters = parameters;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?>? Parameters => _parameters;
}
