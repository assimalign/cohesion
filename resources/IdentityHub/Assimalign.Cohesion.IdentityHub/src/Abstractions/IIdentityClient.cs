using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// An app registration: one application the identity hub knows, with how it signs in, what it
/// proves its identity with, the API it exposes, and the permissions it holds on other APIs.
/// </summary>
/// <remarks>
/// <para>
/// A registration plays either or both of two parts. As a <b>client</b> it obtains tokens: it
/// lists the grants it may use (<see cref="Grants"/>), where a user may be sent back to
/// (<see cref="Redirects"/>), what it authenticates with (<see cref="Credentials"/>), and what
/// it may be issued (<see cref="Permissions"/>). As a <b>resource</b> it exposes an API: the
/// audiences tokens for it carry (<see cref="Audiences"/>), and the scopes and roles other
/// registrations may hold on it (<see cref="Scopes"/>, <see cref="Roles"/>).
/// </para>
/// <para>
/// <b>A registration is immutable.</b> Changing one is storing a replacement through an
/// <see cref="IIdentityClientRegistry"/>, which is also what says whether it may change at all:
/// a registration defined in code is <see cref="IsLocked"/>, and the registry refuses every
/// write to it.
/// </para>
/// <para>
/// <see cref="IdentityClient"/> is the implementation, created from an
/// <see cref="IdentityClientDescriptor"/>. A registry does not trust another implementation:
/// it copies one through the same validation before it keeps or stores it.
/// </para>
/// </remarks>
public interface IIdentityClient
{
    /// <summary>
    /// Gets the registration's identifier: the OAuth <c>client_id</c>.
    /// </summary>
    IdentityClientId Id { get; }

    /// <summary>
    /// Gets the name a person is shown for the application.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets what the application is, in words a person is shown, when it is described.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Gets a value indicating whether the registration is defined in code. A locked
    /// registration is read like any other and changes only where it is defined: its registry
    /// answers every write to it with <see cref="IdentityClientWriteStatus.Locked"/>.
    /// </summary>
    /// <remarks>
    /// The registry that serves a registration sets this value. It is <see langword="false"/> on
    /// a registration no registry has served yet.
    /// </remarks>
    bool IsLocked { get; }

    /// <summary>
    /// Gets the version of the stored state the registration was read from, which a replace or a
    /// remove names to act only on that state. It is <see langword="null"/> for a locked
    /// registration and for one no registry has served yet.
    /// </summary>
    IdentityHubRecordVersion? Version { get; }

    /// <summary>
    /// Gets a value indicating whether the application can authenticate itself: it holds at
    /// least one credential. A registration without one is a public client.
    /// </summary>
    bool IsConfidential { get; }

    /// <summary>
    /// Gets the grants the application may use to obtain tokens.
    /// </summary>
    IdentityClientGrants Grants { get; }

    /// <summary>
    /// Gets how long an access token issued to the application is valid.
    /// </summary>
    TimeSpan AccessTokenLifetime { get; }

    /// <summary>
    /// Gets the URIs a user may be sent back to after the authorization code grant.
    /// </summary>
    IReadOnlyList<IdentityClientRedirect> Redirects { get; }

    /// <summary>
    /// Gets what the application proves its identity with.
    /// </summary>
    IReadOnlyList<IdentityClientCredential> Credentials { get; }

    /// <summary>
    /// Gets the audiences of the API the registration exposes. No other registration exposes
    /// any of them.
    /// </summary>
    IReadOnlyList<IdentityClientAudience> Audiences { get; }

    /// <summary>
    /// Gets the scopes of the API the registration exposes.
    /// </summary>
    IReadOnlyList<IdentityClientScope> Scopes { get; }

    /// <summary>
    /// Gets the roles of the API the registration exposes.
    /// </summary>
    IReadOnlyList<IdentityClientRole> Roles { get; }

    /// <summary>
    /// Gets the permissions the application holds on APIs: the most it may be issued.
    /// </summary>
    IReadOnlyList<IdentityClientPermission> Permissions { get; }
}
