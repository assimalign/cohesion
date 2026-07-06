using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Assimalign.Cohesion.Connections.InMemory;

/// <summary>
/// A logical, name-addressed endpoint for the in-memory transport.
/// </summary>
/// <remarks>
/// The in-memory transport has no operating-system address space, so its connections are bound to a
/// name rather than an <see cref="IPEndPoint"/>. Two <see cref="InMemoryEndPoint"/> values are equal
/// when their <see cref="Name"/> matches; a listener and the factory that dials it therefore agree
/// on identity by name. Ephemeral client-side endpoints are minted with <see cref="CreateEphemeral"/>
/// so each dialed connection reports a distinct local endpoint that mirrors across the pair.
/// </remarks>
public sealed class InMemoryEndPoint : EndPoint, IEquatable<InMemoryEndPoint>
{
    private static long ephemeralCounter;

    /// <summary>
    /// The default endpoint name used when a listener is created without an explicit endpoint.
    /// </summary>
    public const string DefaultName = "in-memory";

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryEndPoint"/> class.
    /// </summary>
    /// <param name="name">The endpoint name that identifies this in-memory address.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public InMemoryEndPoint(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("The in-memory endpoint name must be a non-empty string.", nameof(name));
        }

        Name = name;
    }

    /// <summary>
    /// Gets the name that identifies this in-memory endpoint.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the address family of the endpoint, which is always <see cref="AddressFamily.Unspecified"/>
    /// because the in-memory transport does not use an operating-system address family.
    /// </summary>
    public override AddressFamily AddressFamily => AddressFamily.Unspecified;

    /// <summary>
    /// Creates a unique ephemeral endpoint, used for the client (dialing) side of a connection so it
    /// reports a distinct local endpoint that mirrors as the remote endpoint of the accepted peer.
    /// </summary>
    /// <param name="baseName">An optional base name; the default is <c>"in-memory:client"</c>.</param>
    /// <returns>A new, uniquely named <see cref="InMemoryEndPoint"/>.</returns>
    public static InMemoryEndPoint CreateEphemeral(string baseName = "in-memory:client")
    {
        long id = Interlocked.Increment(ref ephemeralCounter);

        return new InMemoryEndPoint($"{baseName}#{id}");
    }

    /// <inheritdoc />
    public bool Equals(InMemoryEndPoint? other)
        => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as InMemoryEndPoint);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => $"memory://{Name}";
}
