using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base for a byte channel, sharing the root's lifecycle and lazy feature plumbing.
/// </summary>
public abstract class DuplexConnection : Connection, IDuplexConnection
{
    /// <summary>Gets the reader for bytes received from the peer.</summary>
    public abstract PipeReader Input { get; }

    /// <summary>Gets the writer for bytes sent to the peer.</summary>
    public abstract PipeWriter Output { get; }

    /// <inheritdoc />
    /// <remarks>The default is bidirectional; unidirectional streams override this property.</remarks>
    public virtual ConnectionDirection Direction => ConnectionDirection.Bidirectional;
}
