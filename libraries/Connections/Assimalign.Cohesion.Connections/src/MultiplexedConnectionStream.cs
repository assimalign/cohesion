using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IMultiplexedConnectionStream"/>.
/// </summary>
/// <remarks>
/// Inherits the connection lifecycle, pipe contracts, bidirectional default, and lazy feature
/// collection. Drivers implement clean input completion and whole-stream reset semantics.
/// </remarks>
public abstract class MultiplexedConnectionStream : DuplexConnection, IMultiplexedConnectionStream
{
    /// <inheritdoc />
    public abstract bool IsInputCompleted { get; }

    /// <inheritdoc />
    public abstract void Abort(long errorCode, Exception? reason = null);
}
