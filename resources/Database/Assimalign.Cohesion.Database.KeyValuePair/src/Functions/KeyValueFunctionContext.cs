using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Identifies the database and cancellation of one scalar call, without exposing storage or a session.</summary>
public readonly ref struct KeyValueFunctionContext
{
    /// <summary>Initializes a call context, including for direct function tests.</summary>
    /// <param name="database">The receiving database; default for a direct call without a database.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    public KeyValueFunctionContext(DatabaseName database, CancellationToken cancellationToken)
    {
        Database = database;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the receiving database, or default outside an engine call.</summary>
    public DatabaseName Database { get; }

    /// <summary>Gets the statement's cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
}
