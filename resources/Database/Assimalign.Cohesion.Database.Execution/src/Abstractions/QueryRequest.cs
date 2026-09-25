using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a query request to be executed against a database.
/// </summary>
public abstract class QueryRequest
{
    /// <summary>
    /// Initializes a new <see cref="QueryRequest"/> with the specified statement.
    /// </summary>
    /// <param name="statement">The parsed query statement.</param>
    protected QueryRequest(QueryStatement statement)
    {
        Statement = statement;
    }

    /// <summary>
    /// Gets the parsed query statement.
    /// </summary>
    public virtual QueryStatement Statement { get; }

    /// <summary>
    /// Gets the parameters to bind to the query, if any.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object?>? Parameters => null;
}

/// <summary>
/// Represents a strongly-typed query request with a specific statement type.
/// </summary>
/// <typeparam name="TStatement">The concrete statement type.</typeparam>
public abstract class QueryRequest<TStatement> : QueryRequest where TStatement : QueryStatement
{
    /// <summary>
    /// Initializes a new <see cref="QueryRequest{TStatement}"/> with the specified statement.
    /// </summary>
    /// <param name="statement">The parsed query statement.</param>
    protected QueryRequest(TStatement statement) : base(statement)
    {
    }

    /// <summary>
    /// Gets the strongly-typed parsed query statement.
    /// </summary>
    public new TStatement Statement => (TStatement)base.Statement;
}
