using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>Accepts connections of a declared kind through the same queue as its untyped view.</summary>
/// <typeparam name="TConnection">The kind of connection the listener produces.</typeparam>
public interface IConnectionListener<TConnection> : IConnectionListener where TConnection : IConnection
{
    /// <summary>Gets the same live collection as the untyped listener, with its declared connection type.</summary>
    /// <remarks>Entries leave when their connection-closed token fires; enumeration tolerates concurrent changes.</remarks>
    new IReadOnlyCollection<TConnection> Connections { get; }

    /// <summary>Accepts the next connection from the listener's shared queue and transfers ownership to the caller.</summary>
    /// <param name="cancellationToken">A token that cancels this accept operation.</param>
    /// <returns>The accepted connection, ready for its owner to use.</returns>
    new ValueTask<TConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
