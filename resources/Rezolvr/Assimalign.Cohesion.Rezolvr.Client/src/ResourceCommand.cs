using System;

namespace Assimalign.Cohesion.Rezolvr.Client;

/// <summary>
/// Carries one generic declarative command to a Rezolvr control plane.
/// </summary>
public sealed record ResourceCommand
{
    /// <summary>
    /// Initializes a resource command.
    /// </summary>
    /// <param name="id">The command's idempotency identifier.</param>
    /// <param name="kind">The area-defined command kind.</param>
    /// <param name="owner">The application or trust principal that owns the command.</param>
    /// <param name="key">The stable area-defined command key.</param>
    /// <param name="payload">The area-defined payload bytes.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/>, <paramref name="kind"/>, <paramref name="owner"/>, or
    /// <paramref name="key"/> is empty or whitespace.
    /// </exception>
    public ResourceCommand(
        string id,
        string kind,
        string owner,
        string key,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Id = id;
        Kind = kind;
        Owner = owner;
        Key = key;
        Payload = payload;
    }

    /// <summary>
    /// Gets the command's idempotency identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the area-defined command kind.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Gets the application or trust principal that owns the command.
    /// </summary>
    public string Owner { get; }

    /// <summary>
    /// Gets the stable area-defined command key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the area-defined payload bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }
}
