using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>One authenticated graph connection supporting one active exchange at a time.</summary>
/// <remarks>Scalar results are materialized. Paths stream until enumeration completes or is disposed.
/// An unfinished path exchange is discarded. Explicit graph transactions are not supported.</remarks>
public interface IGraphConnection : IAsyncDisposable
{
    /// <summary>Gets the database selected during the handshake.</summary>
    string Database { get; }

    /// <summary>Gets whether the connection is open and available for use.</summary>
    bool IsOpen { get; }

    /// <summary>Executes a scalar projection or catalog statement and materializes its rows.</summary>
    /// <param name="statement">The GQL statement.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The columns, rows, and affected count.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    ValueTask<GraphResultSet> QueryAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a graph mutation and returns its affected entity count.</summary>
    /// <param name="statement">The GQL statement.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The affected count, or -1 for a row-returning statement.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    ValueTask<long> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Streams paths selected by a single bound path, node, or relationship variable.</summary>
    /// <param name="statement">A MATCH statement returning one bound path or entity variable.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the full enumeration.</param>
    /// <returns>Paths retaining node and relationship identities, labels, types, and properties.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="GraphClientException">The query or stream failed; its connection is discarded.</exception>
    /// <remarks>Enumerate to completion to verify the server's terminal path count. Disposing an
    /// unfinished enumeration cancels the exchange and prevents reuse of that session.</remarks>
    IAsyncEnumerable<GraphPath> QueryPathsAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Discards the rental and closes its session without returning it for reuse.</summary>
    /// <returns>The noncancellable teardown operation.</returns>
    ValueTask AbortAsync();
}

