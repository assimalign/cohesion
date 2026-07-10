using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Connections.NamedPipes;

/// <summary>
/// A named-pipe endpoint, identified by a pipe name and the host that owns the pipe.
/// </summary>
/// <remarks>
/// A named pipe has no operating-system address space, so its connections are addressed by name
/// rather than by an <see cref="IPEndPoint"/>. A <see cref="NamedPipeConnectionListener"/> can only
/// create a server on the local host (<see cref="LocalServerName"/>); a
/// <see cref="NamedPipeConnectionFactory"/> may dial a pipe on a remote host by setting
/// <see cref="ServerName"/>. Names compare case-insensitively, matching Windows pipe-name semantics.
/// </remarks>
public sealed class NamedPipeEndPoint : EndPoint, IEquatable<NamedPipeEndPoint>
{
    /// <summary>
    /// The server name that denotes the local host (<c>"."</c>).
    /// </summary>
    public const string LocalServerName = ".";

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeEndPoint"/> class.
    /// </summary>
    /// <param name="pipeName">The name of the pipe (the segment after <c>\\server\pipe\</c>).</param>
    /// <param name="serverName">
    /// The host that owns the pipe; defaults to <see cref="LocalServerName"/> (the local host). A
    /// listener requires the local host; a factory may target a remote host.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="pipeName"/> or <paramref name="serverName"/> is <see langword="null"/> or empty.
    /// </exception>
    public NamedPipeEndPoint(string pipeName, string serverName = LocalServerName)
    {
        if (string.IsNullOrEmpty(pipeName))
        {
            throw new ArgumentException("The pipe name must be a non-empty string.", nameof(pipeName));
        }

        if (string.IsNullOrEmpty(serverName))
        {
            throw new ArgumentException("The server name must be a non-empty string.", nameof(serverName));
        }

        PipeName = pipeName;
        ServerName = serverName;
    }

    /// <summary>
    /// Gets the host that owns the pipe. <c>"."</c> denotes the local host.
    /// </summary>
    public string ServerName { get; }

    /// <summary>
    /// Gets the name of the pipe.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Gets whether this endpoint refers to a pipe on the local host.
    /// </summary>
    public bool IsLocal => string.Equals(ServerName, LocalServerName, StringComparison.Ordinal);

    /// <summary>
    /// Gets the address family of the endpoint, which is always <see cref="AddressFamily.Unspecified"/>
    /// because a named pipe does not use an operating-system address family.
    /// </summary>
    public override AddressFamily AddressFamily => AddressFamily.Unspecified;

    /// <inheritdoc />
    public bool Equals(NamedPipeEndPoint? other)
        => other is not null
            && string.Equals(ServerName, other.ServerName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(PipeName, other.PipeName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as NamedPipeEndPoint);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(ServerName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(PipeName));

    /// <inheritdoc />
    public override string ToString() => $@"\\{ServerName}\pipe\{PipeName}";
}
