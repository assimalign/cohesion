using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An authenticated caller of a gateway control plane. The gateway applies its area and command
/// checks to this mapped identity.
/// </summary>
/// <param name="Application">
/// The caller's application for a <see cref="ApplicationCallerKind.Peer"/> caller, or
/// <see langword="null"/> when the caller acts for no application.
/// </param>
/// <param name="Subject">The caller's authority-qualified stable subject identifier.</param>
/// <param name="Kind">Whether the caller is a peer gateway, developer or operator.</param>
/// <param name="AllowedCommandKinds">
/// The wire command kinds the caller may apply or delete. An empty list permits every kind, the
/// same rule as <see cref="TrustedIssuer.AllowedCommandKinds"/>; an authenticator that grants no
/// command kinds returns <see cref="ApplicationCallerStatus.Forbidden"/> instead of an empty list.
/// Resource state management uses the separate management grants. The list is copied.
/// </param>
/// <param name="AllowedManagementOperations">Explicit gateway operation grants; null or empty grants no state management access. These strings are opaque to model composition.</param>
/// <exception cref="ArgumentException"><paramref name="Subject"/> is <see langword="null"/>, empty, or whitespace.</exception>
/// <exception cref="ArgumentNullException"><paramref name="AllowedCommandKinds"/> is <see langword="null"/>.</exception>
[method: JsonConstructor]
public sealed record ApplicationCaller(
    ApplicationName? Application,
    string Subject,
    ApplicationCallerKind Kind,
    IReadOnlyList<string> AllowedCommandKinds,
    IReadOnlyList<string>? AllowedManagementOperations)
{
    /// <summary>Creates a caller with the existing command grants and no management grants.</summary>
    /// <param name="Application">The optional caller application.</param>
    /// <param name="Subject">The stable caller subject.</param>
    /// <param name="Kind">The caller category.</param>
    /// <param name="AllowedCommandKinds">The existing wire-command grants.</param>
    /// <exception cref="ArgumentException">The subject is invalid.</exception>
    /// <exception cref="ArgumentNullException">The command list is null.</exception>
    public ApplicationCaller(ApplicationName? Application, string Subject, ApplicationCallerKind Kind, IReadOnlyList<string> AllowedCommandKinds)
        : this(Application, Subject, Kind, AllowedCommandKinds, null)
    {
    }

    /// <summary>Deconstructs the caller using the existing four-value contract.</summary>
    /// <param name="application">The optional caller application.</param>
    /// <param name="subject">The stable subject.</param>
    /// <param name="kind">The caller category.</param>
    /// <param name="allowedCommandKinds">The command grants.</param>
    public void Deconstruct(out ApplicationName? application, out string subject, out ApplicationCallerKind kind, out IReadOnlyList<string> allowedCommandKinds)
    {
        application = Application;
        subject = Subject;
        kind = Kind;
        allowedCommandKinds = AllowedCommandKinds;
    }

    /// <summary>
    /// Gets the caller's stable subject identifier.
    /// </summary>
    public string Subject { get; init; } = RequireSubject(Subject);

    /// <summary>
    /// Gets the wire command kinds the caller may apply or delete; empty permits every kind.
    /// These grants confer no resource state management access.
    /// </summary>
    public IReadOnlyList<string> AllowedCommandKinds { get; init; } = CopyKinds(AllowedCommandKinds);

    /// <summary>Gets explicit gateway operation grants, copied from the authenticated policy.</summary>
    public IReadOnlyList<string> AllowedManagementOperations { get; init; } = CopyKinds(AllowedManagementOperations ?? Array.Empty<string>());

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
