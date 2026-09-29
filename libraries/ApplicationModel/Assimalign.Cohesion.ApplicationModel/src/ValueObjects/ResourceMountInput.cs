using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The result of resolving one resource mount source for a reconcile pass.
/// </summary>
public sealed class ResourceMountInput
{
    private ResourceMountInput(
        string? source,
        ReadOnlyMemory<byte> content,
        bool isResolved,
        string? unresolvedReason)
    {
        Source = source;
        Content = Copy(content);
        IsResolved = isResolved;
        UnresolvedReason = unresolvedReason;
    }

    /// <summary>Gets the source expression from the resource plan, when one was declared.</summary>
    public string? Source { get; }

    /// <summary>Gets whether the source was resolved successfully.</summary>
    public bool IsResolved { get; }

    /// <summary>Gets an immutable copy of the resolved content, or empty content when unresolved.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>Gets the actionable resolution failure, or <see langword="null"/> when resolved.</summary>
    public string? UnresolvedReason { get; }

    /// <summary>Creates a successfully resolved mount input.</summary>
    /// <param name="source">The source expression, or <see langword="null"/> for an empty input.</param>
    /// <param name="content">The resolved content. The value is defensively copied.</param>
    /// <returns>An immutable resolved input.</returns>
    public static ResourceMountInput Resolved(string? source, ReadOnlyMemory<byte> content) =>
        new(source, content, isResolved: true, unresolvedReason: null);

    /// <summary>Creates a typed unresolved mount input.</summary>
    /// <param name="source">The source expression that could not be resolved.</param>
    /// <param name="reason">The actionable resolution failure.</param>
    /// <returns>An immutable unresolved input.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> or <paramref name="reason"/> is empty.
    /// </exception>
    public static ResourceMountInput Unresolved(string source, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ResourceMountInput(source, ReadOnlyMemory<byte>.Empty, isResolved: false, reason);
    }

    private static ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return source.ToArray();
    }
}
