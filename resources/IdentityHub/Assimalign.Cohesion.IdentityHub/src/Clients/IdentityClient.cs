using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.IdentityHub.Internal;

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
/// <b>A registration is the validated, immutable snapshot of an
/// <see cref="IdentityClientDescriptor"/>.</b> <see cref="Create(IdentityClientDescriptor)"/> is
/// the one place a registration is checked as a whole, and the only way one comes to exist. A
/// registration that exists is therefore consistent: a grant has what it needs, a name appears
/// once, and every list is within its limit. The same check runs when a stored registration is
/// read back, so the limits may grow in a later version and never shrink.
/// </para>
/// <para>
/// Changing a registration is storing a replacement through an
/// <see cref="IIdentityClientRegistry"/>, which is also what says whether it may change at all.
/// A registration written in code is handed to an <see cref="IdentityClientRegistry"/>, which
/// serves it <see cref="IsLocked"/> and refuses every write to it. One created at run time is
/// stored through the same registry.
/// </para>
/// <para>
/// There is no interface for a registration. It is a value with one set of rules, not a
/// behavior with variants: a second implementation could only weaken what
/// <see cref="Create(IdentityClientDescriptor)"/> guarantees.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IdentityClient api = IdentityClient.Create(new()
/// {
///     Id = IdentityClientId.FromName("orders-api"),
///     DisplayName = "Orders API",
///     Audiences = { "api://orders" },
///     Scopes = { IdentityClientScope.Create("orders.read") },
/// });
/// </code>
/// </example>
// Deviates from the repo interface-first rule per design decision: an app registration is a
// validated value with one fixed behavior, so it ships as a concrete type with no interface
// (owner, 2026-11-20).
public sealed class IdentityClient
{
    /// <summary>
    /// The longest display name, in characters.
    /// </summary>
    public const int MaxDisplayNameLength = IdentityClientGrammar.MaxDisplayNameLength;

    /// <summary>
    /// The longest description, in characters.
    /// </summary>
    public const int MaxDescriptionLength = IdentityClientGrammar.MaxDescriptionLength;

    /// <summary>
    /// The most redirects a registration lists.
    /// </summary>
    public const int MaxRedirectCount = 64;

    /// <summary>
    /// The most credentials a registration holds.
    /// </summary>
    public const int MaxCredentialCount = 16;

    /// <summary>
    /// The most audiences a registration exposes.
    /// </summary>
    public const int MaxAudienceCount = 8;

    /// <summary>
    /// The most scopes a registration exposes.
    /// </summary>
    public const int MaxScopeCount = 64;

    /// <summary>
    /// The most roles a registration exposes.
    /// </summary>
    public const int MaxRoleCount = 64;

    /// <summary>
    /// The most permissions a registration holds.
    /// </summary>
    public const int MaxPermissionCount = 128;

    private const IdentityClientGrants allGrants =
        IdentityClientGrants.AuthorizationCode |
        IdentityClientGrants.ClientCredentials |
        IdentityClientGrants.DeviceCode |
        IdentityClientGrants.RefreshToken;

    private IdentityClient(
        IdentityClientId id,
        string displayName,
        string? description,
        IdentityClientGrants grants,
        TimeSpan accessTokenLifetime,
        IReadOnlyList<IdentityClientRedirect> redirects,
        IReadOnlyList<IdentityClientCredential> credentials,
        IReadOnlyList<IdentityClientAudience> audiences,
        IReadOnlyList<IdentityClientScope> scopes,
        IReadOnlyList<IdentityClientRole> roles,
        IReadOnlyList<IdentityClientPermission> permissions)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Grants = grants;
        AccessTokenLifetime = accessTokenLifetime;
        Redirects = redirects;
        Credentials = credentials;
        Audiences = audiences;
        Scopes = scopes;
        Roles = roles;
        Permissions = permissions;
    }

    private IdentityClient(IdentityClient source, bool isLocked, IdentityHubRecordVersion? version)
        : this(
            source.Id,
            source.DisplayName,
            source.Description,
            source.Grants,
            source.AccessTokenLifetime,
            source.Redirects,
            source.Credentials,
            source.Audiences,
            source.Scopes,
            source.Roles,
            source.Permissions)
    {
        IsLocked = isLocked;
        Version = version;
    }

    /// <summary>
    /// Gets how long an access token is valid when a registration does not say: one hour.
    /// </summary>
    public static TimeSpan DefaultAccessTokenLifetime { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the longest an access token may be valid: 24 hours.
    /// </summary>
    public static TimeSpan MaxAccessTokenLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the registration's identifier: the OAuth <c>client_id</c>.
    /// </summary>
    public IdentityClientId Id { get; }

    /// <summary>
    /// Gets the name a person is shown for the application. It is the identifier's text when
    /// the registration did not say.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets what the application is, in words a person is shown, when it is described.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets a value indicating whether the registration is defined in code. A locked
    /// registration is read like any other and changes only where it is defined: its registry
    /// answers every write to it with <see cref="IdentityClientWriteStatus.Locked"/>.
    /// </summary>
    /// <remarks>
    /// The registry that serves a registration sets this value. It is <see langword="false"/> on
    /// a registration no registry has served yet.
    /// </remarks>
    public bool IsLocked { get; }

    /// <summary>
    /// Gets the version of the stored state the registration was read from, which a replace or a
    /// remove names to act only on that state. It is <see langword="null"/> for a locked
    /// registration and for one no registry has served yet.
    /// </summary>
    public IdentityHubRecordVersion? Version { get; }

    /// <summary>
    /// Gets a value indicating whether the application can authenticate itself: it holds at
    /// least one credential. A registration without one is a public client.
    /// </summary>
    public bool IsConfidential => Credentials.Count > 0;

    /// <summary>
    /// Gets the grants the application may use to obtain tokens.
    /// </summary>
    public IdentityClientGrants Grants { get; }

    /// <summary>
    /// Gets how long an access token issued to the application is valid.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; }

    /// <summary>
    /// Gets the URIs a user may be sent back to after the authorization code grant.
    /// </summary>
    public IReadOnlyList<IdentityClientRedirect> Redirects { get; }

    /// <summary>
    /// Gets what the application proves its identity with.
    /// </summary>
    public IReadOnlyList<IdentityClientCredential> Credentials { get; }

    /// <summary>
    /// Gets the audiences of the API the registration exposes. No other registration exposes
    /// any of them.
    /// </summary>
    public IReadOnlyList<IdentityClientAudience> Audiences { get; }

    /// <summary>
    /// Gets the scopes of the API the registration exposes.
    /// </summary>
    public IReadOnlyList<IdentityClientScope> Scopes { get; }

    /// <summary>
    /// Gets the roles of the API the registration exposes.
    /// </summary>
    public IReadOnlyList<IdentityClientRole> Roles { get; }

    /// <summary>
    /// Gets the permissions the application holds on APIs: the most it may be issued.
    /// </summary>
    public IReadOnlyList<IdentityClientPermission> Permissions { get; }

    /// <summary>
    /// Creates a registration from a descriptor, checking it as a whole.
    /// </summary>
    /// <remarks>
    /// <para>The registration is refused when:</para>
    /// <list type="bullet">
    /// <item><description>its identifier is missing, or its display name or description breaks its rule;</description></item>
    /// <item><description>a list is longer than its limit, holds an empty entry, or names the same redirect, credential, audience, scope, role, or permission twice;</description></item>
    /// <item><description>it uses <see cref="IdentityClientGrants.AuthorizationCode"/> and lists no redirect, or lists a redirect without using that grant;</description></item>
    /// <item><description>it uses <see cref="IdentityClientGrants.ClientCredentials"/> and holds no credential;</description></item>
    /// <item><description>it uses <see cref="IdentityClientGrants.RefreshToken"/> with neither <see cref="IdentityClientGrants.AuthorizationCode"/> nor <see cref="IdentityClientGrants.DeviceCode"/>;</description></item>
    /// <item><description>it exposes a scope or a role and no audience;</description></item>
    /// <item><description>it holds a permission on itself that names no scope it exposes, or no role it exposes to applications.</description></item>
    /// </list>
    /// <para>
    /// A permission on another registration is not resolved here: the registry checks the ones it
    /// can, and the rest are resolved when a token is issued.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">The contents of the registration. It is copied.</param>
    /// <returns>The registration, not locked and with no version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The registration breaks one of the rules above. The message names the registration and the rule.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The access token lifetime is not greater than zero, or is longer than
    /// <see cref="MaxAccessTokenLifetime"/>.
    /// </exception>
    public static IdentityClient Create(IdentityClientDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        IdentityClientId id = descriptor.Id;
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.ClientIdRequired, nameof(descriptor));
        }

        string? displayName = descriptor.DisplayName;
        if (displayName is not null &&
            !IdentityClientGrammar.IsText(displayName, MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.ClientDisplayNameInvalid, id.ToString());
        }

        // What a message calls the registration. The name an identifier was derived from is not
        // kept, and a GUID alone tells a reader nothing, so the display name leads when there
        // is one.
        string label = Describe(id, displayName);

        string? description = descriptor.Description is { Length: > 0 } ? descriptor.Description : null;
        if (description is not null &&
            !IdentityClientGrammar.IsText(description, MaxDescriptionLength, allowLineBreaks: true))
        {
            throw Refuse(ErrorMessages.ClientDescriptionInvalid, label);
        }

        IdentityClientGrants grants = descriptor.Grants;
        if ((grants & ~allGrants) is not IdentityClientGrants.None)
        {
            throw Refuse(ErrorMessages.ClientGrantsInvalid, label);
        }

        TimeSpan lifetime = descriptor.AccessTokenLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxAccessTokenLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                lifetime,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientLifetimeInvalid, label, MaxAccessTokenLifetime));
        }

        IdentityClientRedirect[] redirects = Snapshot(
            label,
            descriptor.Redirects,
            nameof(descriptor.Redirects),
            MaxRedirectCount,
            static redirect => redirect.Uri,
            ErrorMessages.ClientRedirectDuplicate);

        IdentityClientCredential[] credentials = Snapshot(
            label,
            descriptor.Credentials,
            nameof(descriptor.Credentials),
            MaxCredentialCount,
            static credential => credential?.Id.Value,
            ErrorMessages.ClientCredentialDuplicate);

        IdentityClientAudience[] audiences = Snapshot(
            label,
            descriptor.Audiences,
            nameof(descriptor.Audiences),
            MaxAudienceCount,
            static audience => audience.Value,
            ErrorMessages.ClientAudienceDuplicate);

        IdentityClientScope[] scopes = Snapshot(
            label,
            descriptor.Scopes,
            nameof(descriptor.Scopes),
            MaxScopeCount,
            static scope => scope?.Name.Value,
            ErrorMessages.ClientScopeDuplicate);

        IdentityClientRole[] roles = Snapshot(
            label,
            descriptor.Roles,
            nameof(descriptor.Roles),
            MaxRoleCount,
            static role => role?.Name.Value,
            ErrorMessages.ClientRoleDuplicate);

        IdentityClientPermission[] permissions = Snapshot(
            label,
            descriptor.Permissions,
            nameof(descriptor.Permissions),
            MaxPermissionCount,
            static permission => permission.Resource.Value == Guid.Empty ? null : permission.ToString(),
            ErrorMessages.ClientPermissionDuplicate);

        bool usesAuthorizationCode = (grants & IdentityClientGrants.AuthorizationCode) is not IdentityClientGrants.None;
        if (usesAuthorizationCode && redirects.Length is 0)
        {
            throw Refuse(ErrorMessages.ClientRedirectRequired, label);
        }

        if (!usesAuthorizationCode && redirects.Length is not 0)
        {
            throw Refuse(ErrorMessages.ClientRedirectUnused, label);
        }

        if ((grants & IdentityClientGrants.ClientCredentials) is not IdentityClientGrants.None && credentials.Length is 0)
        {
            throw Refuse(ErrorMessages.ClientCredentialRequired, label);
        }

        if ((grants & IdentityClientGrants.RefreshToken) is not IdentityClientGrants.None &&
            (grants & (IdentityClientGrants.AuthorizationCode | IdentityClientGrants.DeviceCode)) is IdentityClientGrants.None)
        {
            throw Refuse(ErrorMessages.ClientRefreshUnused, label);
        }

        if (audiences.Length is 0 && (scopes.Length is not 0 || roles.Length is not 0))
        {
            throw Refuse(ErrorMessages.ClientAudienceRequired, label);
        }

        var client = new IdentityClient(
            id,
            displayName ?? id.ToString(),
            description,
            grants,
            lifetime,
            Array.AsReadOnly(redirects),
            Array.AsReadOnly(credentials),
            Array.AsReadOnly(audiences),
            Array.AsReadOnly(scopes),
            Array.AsReadOnly(roles),
            Array.AsReadOnly(permissions));

        for (int index = 0; index < permissions.Length; index++)
        {
            IdentityClientPermission permission = permissions[index];
            if (permission.Resource == id && !client.Exposes(permission))
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        permission.Kind is IdentityClientPermissionKind.Delegated
                            ? ErrorMessages.ClientScopeUnknown
                            : ErrorMessages.ClientRoleUnknown,
                        label,
                        permission.Name,
                        label),
                    nameof(descriptor));
            }
        }

        return client;
    }

    /// <summary>
    /// Returns the identifier of the registration.
    /// </summary>
    /// <returns>The identifier of the registration.</returns>
    public override string ToString() => Id.ToString();

    /// <summary>
    /// Returns what a message calls a registration: its display name and its identifier, or the
    /// identifier alone when it has no display name of its own.
    /// </summary>
    internal static string Describe(IdentityClient client)
    {
        string id = client.Id.ToString();
        return Describe(client.Id, string.Equals(client.DisplayName, id, StringComparison.Ordinal) ? null : client.DisplayName);
    }

    /// <summary>
    /// Returns the registration as a registry serves one defined in code.
    /// </summary>
    internal IdentityClient AsLocked()
    {
        return new IdentityClient(this, isLocked: true, version: null);
    }

    /// <summary>
    /// Returns the registration as a registry serves one read from, or about to be written to,
    /// its store.
    /// </summary>
    internal IdentityClient AsStored(IdentityHubRecordVersion? version)
    {
        return new IdentityClient(this, isLocked: false, version);
    }

    /// <summary>
    /// Determines whether the registration exposes what a permission names: a scope for a
    /// delegated permission, a role open to applications for an application permission.
    /// </summary>
    internal bool Exposes(IdentityClientPermission permission)
    {
        if (permission.Kind is IdentityClientPermissionKind.Delegated)
        {
            for (int index = 0; index < Scopes.Count; index++)
            {
                if (Scopes[index].Name == permission.Name)
                {
                    return true;
                }
            }

            return false;
        }

        for (int index = 0; index < Roles.Count; index++)
        {
            IdentityClientRole role = Roles[index];
            if (role.Name == permission.Name &&
                (role.Members & IdentityClientRoleMembers.Applications) is not IdentityClientRoleMembers.None)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(IdentityClientId id, string? displayName)
    {
        return displayName is null ? id.ToString() : string.Concat(displayName, " (", id.ToString(), ")");
    }

    private static T[] Snapshot<T>(
        string label,
        IList<T> source,
        string listName,
        int maxCount,
        Func<T, string?> nameOf,
        string duplicateMessage)
    {
        if (source.Count > maxCount)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientListTooLong, label, listName, maxCount),
                "descriptor");
        }

        if (source.Count is 0)
        {
            return Array.Empty<T>();
        }

        var snapshot = new T[source.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < snapshot.Length; index++)
        {
            T entry = source[index];
            string? name = nameOf(entry);
            if (name is null)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientListEntryRequired, label, listName),
                    "descriptor");
            }

            if (!names.Add(name))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, duplicateMessage, label, name),
                    "descriptor");
            }

            snapshot[index] = entry;
        }

        return snapshot;
    }

    private static ArgumentException Refuse(string message, string label)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, message, label), "descriptor");
    }
}
