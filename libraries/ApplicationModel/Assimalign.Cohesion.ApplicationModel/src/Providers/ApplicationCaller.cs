using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An authenticated caller of a gateway control plane. The gateway applies its area and command
/// checks to this mapped identity.
/// </summary>
/// <param name="Application">
/// The caller's application for a <see cref="ApplicationCallerKind.Peer"/> caller, or
/// <see langword="null"/> when the caller acts for no application.
/// </param>
/// <param name="Subject">The caller's stable subject identifier.</param>
/// <param name="Kind">Whether the caller is a peer gateway or a developer.</param>
/// <param name="AllowedCommandKinds">
/// The wire command kinds the caller may apply or delete. An empty list permits every kind, the
/// same rule as <see cref="TrustedIssuer.AllowedCommandKinds"/>; an authenticator that grants no
/// command kinds returns <see cref="ApplicationCallerStatus.Forbidden"/> instead of an empty list.
/// The list is copied.
/// </param>
/// <exception cref="ArgumentException"><paramref name="Subject"/> is <see langword="null"/>, empty, or whitespace.</exception>
/// <exception cref="ArgumentNullException"><paramref name="AllowedCommandKinds"/> is <see langword="null"/>.</exception>
public sealed record ApplicationCaller(
    ApplicationName? Application,
    string Subject,
    ApplicationCallerKind Kind,
    IReadOnlyList<string> AllowedCommandKinds)
{
    /// <summary>
    /// Gets the caller's stable subject identifier.
    /// </summary>
    public string Subject { get; init; } = RequireSubject(Subject);

    /// <summary>
    /// Gets the wire command kinds the caller may apply or delete; empty permits every kind.
    /// </summary>
    public IReadOnlyList<string> AllowedCommandKinds { get; init; } = CopyKinds(AllowedCommandKinds);

    private static string RequireSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(Subject));
        return subject;
    }

    private static IReadOnlyList<string> CopyKinds(IReadOnlyList<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds, nameof(AllowedCommandKinds));
        var copy = new string[kinds.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = kinds[index];
        }

        return Array.AsReadOnly(copy);
    }
}
