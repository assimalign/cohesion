using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Graph.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A single-use stream of graph paths with statement completion diagnostics.</summary>
/// <remarks>Enumerate to EOF before reading Diagnostics. Early disposal cancels the exchange
/// and discards its connection, as with QueryPathsAsync.</remarks>
[global::System.Diagnostics.DebuggerDisplay("GraphPathResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Client.GraphPathResult.DebugView))]
public sealed class GraphPathResult : IAsyncEnumerable<GraphPath>
{
    private readonly GraphPathsExchange _exchange;
    private readonly IAsyncEnumerable<GraphPath> _paths;
    private int _enumerated;

    internal GraphPathResult(GraphPathsExchange exchange, IAsyncEnumerable<GraphPath> paths)
    {
        _exchange = exchange;
        _paths = paths;
    }

    /// <summary>Gets the negotiated diagnostics, finalized when enumeration reaches EOF.
    /// The collection is empty before diagnostics arrive or when none were sent.</summary>
    public IReadOnlyList<ProtocolDiagnostic> Diagnostics => _exchange.Diagnostics;

    /// <summary>Begins the single enumeration of this statement's paths.</summary>
    /// <param name="cancellationToken">Cancellation token for enumeration.</param>
    /// <returns>The asynchronous path enumerator.</returns>
    /// <exception cref="InvalidOperationException">This result has already been enumerated.</exception>
    public IAsyncEnumerator<GraphPath> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.PathResultAlreadyEnumerated);
        }
        return _paths.GetAsyncEnumerator(cancellationToken);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Client.GraphPathResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Client.GraphPathResult value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Protocol.ProtocolDiagnostic> Diagnostics => _target.Diagnostics;
    }
}
