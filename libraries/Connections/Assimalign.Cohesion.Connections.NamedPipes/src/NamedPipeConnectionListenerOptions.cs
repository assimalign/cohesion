using System;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Assimalign.Cohesion.Connections.NamedPipes;

/// <summary>
/// Provides binding and pipe-tuning options for <see cref="NamedPipeConnectionListener"/>.
/// </summary>
public sealed class NamedPipeConnectionListenerOptions
{
    /// <summary>
    /// Gets or sets the endpoint the listener binds to. The endpoint must name a pipe on the local
    /// host (<see cref="NamedPipeEndPoint.IsLocal"/>); a server cannot be created on a remote host.
    /// </summary>
    /// <remarks>
    /// There is no default pipe name; this must be set before the listener binds.
    /// </remarks>
    public NamedPipeEndPoint? EndPoint { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent server instances that may share the pipe name.
    /// </summary>
    /// <remarks>
    /// Each live accepted connection holds one instance; a listener that is continuously accepting also
    /// keeps one instance waiting for the next client. Defaults to
    /// <see cref="NamedPipeServerStream.MaxAllowedServerInstances"/> (unlimited), so the connection
    /// count is bounded by the consumer, not the transport.
    /// </remarks>
    public int MaxServerInstances { get; set; } = NamedPipeServerStream.MaxAllowedServerInstances;

    /// <summary>
    /// Gets or sets whether the pipe is created in write-through mode, so a write does not return until
    /// it has been transmitted to the other end. Defaults to <see langword="false"/>.
    /// </summary>
    public bool WriteThrough { get; set; }

    /// <summary>
    /// Gets or sets whether the pipe is restricted to the current user, rejecting connections from
    /// clients running as any other user. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This is a convenience that applies a current-user-only access-control policy without an explicit
    /// <see cref="PipeSecurity"/>. It is ignored when <see cref="PipeSecurity"/> is set, since an
    /// explicit ACL fully specifies the policy.
    /// </remarks>
    public bool CurrentUserOnly { get; set; }

    /// <summary>
    /// Gets or sets the Windows access-control policy applied to the server pipe. When set, the pipe is
    /// created with this ACL, giving builder-time control over which principals may connect.
    /// </summary>
    /// <remarks>
    /// Windows-only. Assigning a non-<see langword="null"/> value on a non-Windows host causes the
    /// listener to fail at bind with <see cref="System.PlatformNotSupportedException"/> rather than
    /// silently dropping the requested access control.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public PipeSecurity? PipeSecurity { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of the inbound (client-to-server) pipe buffer.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="DefaultBufferSize"/>. A non-zero buffer lets a peer's small writes complete
    /// without this end actively reading, giving the pipe socket-like semantics; a value of <c>0</c>
    /// requests the operating-system default, under which a write can block until the reader drains it.
    /// </remarks>
    public int InputBufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// Gets or sets the size, in bytes, of the outbound (server-to-client) pipe buffer.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="DefaultBufferSize"/>. See <see cref="InputBufferSize"/> for the effect of
    /// the buffer size on write behavior.
    /// </remarks>
    public int OutputBufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// The default inbound and outbound pipe buffer size, in bytes (4&#160;KiB).
    /// </summary>
    public const int DefaultBufferSize = 4096;

    /// <summary>
    /// Creates a new <see cref="NamedPipeConnectionListenerOptions"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the options.</param>
    /// <returns>The configured options.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static NamedPipeConnectionListenerOptions Create(Action<NamedPipeConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        NamedPipeConnectionListenerOptions options = new();
        configure(options);

        return options;
    }
}
