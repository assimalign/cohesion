using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// An app registration: the validated, immutable snapshot of an
/// <see cref="IdentityClientDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create(IdentityClientDescriptor)"/> is the one place a registration is checked as
/// a whole. A registration that exists is therefore consistent: a grant has what it needs, a
/// name appears once, and every list is within its limit. The same check runs when a stored
/// registration is read back, so the limits may grow in a later version and never shrink.
/// </para>
/// <para>
/// A registration written in code is handed to an <see cref="IdentityClientRegistry"/>, which
/// serves it locked. One created at run time is stored through the same registry.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IdentityClient api = IdentityClient.Create(new()
/// {
///     Id = "orders-api",
///     Audiences = { "api://orders" },
///     Scopes = { IdentityClientScope.Create("orders.read") },
/// });
/// </code>
/// </example>
public sealed class IdentityClient : IIdentityClient
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

    /// <inheritdoc />
    public IdentityClientId Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public string? Description { get; }

    /// <inheritdoc />
    public bool IsLocked { get; }

    /// <inheritdoc />
    public IdentityHubRecordVersion? Version { get; }

    /// <inheritdoc />
    public bool IsConfidential => Credentials.Count > 0;

    /// <inheritdoc />
    public IdentityClientGrants Grants { get; }

    /// <inheritdoc />
    public TimeSpan AccessTokenLifetime { get; }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClientRedirect> Redirects { get; }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClientCredential> Credentials { get; }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClientAudience> Audiences { get; }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClientScope> Scopes { get; }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClientRole> Roles { get; }

    /// <inheritdoc />
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
        if (id.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ClientIdRequired, nameof(descriptor));
        }

        string? displayName = descriptor.DisplayName;
        if (displayName is not null &&
            !IdentityClientGrammar.IsText(displayName, MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.ClientDisplayNameInvalid, id);
        }

        string? description = descriptor.Description is { Length: > 0 } ? descriptor.Description : null;
        if (description is not null &&
            !IdentityClientGrammar.IsText(description, MaxDescriptionLength, allowLineBreaks: true))
        {
            throw Refuse(ErrorMessages.ClientDescriptionInvalid, id);
        }

        IdentityClientGrants grants = descriptor.Grants;
        if ((grants & ~allGrants) is not IdentityClientGrants.None)
        {
            throw Refuse(ErrorMessages.ClientGrantsInvalid, id);
        }

        TimeSpan lifetime = descriptor.AccessTokenLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxAccessTokenLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                lifetime,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientLifetimeInvalid, id, MaxAccessTokenLifetime));
        }

        IdentityClientRedirect[] redirects = Snapshot(
            id,
            descriptor.Redirects,
            nameof(descriptor.Redirects),
            MaxRedirectCount,
            static redirect => redirect.Uri,
            ErrorMessages.ClientRedirectDuplicate);

        IdentityClientCredential[] credentials = Snapshot(
            id,
            descriptor.Credentials,
            nameof(descriptor.Credentials),
            MaxCredentialCount,
            static credential => credential?.Id.Value,
            ErrorMessages.ClientCredentialDuplicate);

        IdentityClientAudience[] audiences = Snapshot(
            id,
            descriptor.Audiences,
            nameof(descriptor.Audiences),
            MaxAudienceCount,
            static audience => audience.Value,
            ErrorMessages.ClientAudienceDuplicate);

        IdentityClientScope[] scopes = Snapshot(
            id,
            descriptor.Scopes,
            nameof(descriptor.Scopes),
            MaxScopeCount,
            static scope => scope?.Name.Value,
            ErrorMessages.ClientScopeDuplicate);

        IdentityClientRole[] roles = Snapshot(
            id,
            descriptor.Roles,
            nameof(descriptor.Roles),
            MaxRoleCount,
            static role => role?.Name.Value,
            ErrorMessages.ClientRoleDuplicate);

        IdentityClientPermission[] permissions = Snapshot(
            id,
            descriptor.Permissions,
            nameof(descriptor.Permissions),
            MaxPermissionCount,
            static permission => permission.Resource.Value is null ? null : permission.ToString(),
            ErrorMessages.ClientPermissionDuplicate);

        bool usesAuthorizationCode = (grants & IdentityClientGrants.AuthorizationCode) is not IdentityClientGrants.None;
        if (usesAuthorizationCode && redirects.Length is 0)
        {
            throw Refuse(ErrorMessages.ClientRedirectRequired, id);
        }

        if (!usesAuthorizationCode && redirects.Length is not 0)
        {
            throw Refuse(ErrorMessages.ClientRedirectUnused, id);
        }

        if ((grants & IdentityClientGrants.ClientCredentials) is not IdentityClientGrants.None && credentials.Length is 0)
        {
            throw Refuse(ErrorMessages.ClientCredentialRequired, id);
        }

        if ((grants & IdentityClientGrants.RefreshToken) is not IdentityClientGrants.None &&
            (grants & (IdentityClientGrants.AuthorizationCode | IdentityClientGrants.DeviceCode)) is IdentityClientGrants.None)
        {
            throw Refuse(ErrorMessages.ClientRefreshUnused, id);
        }

        if (audiences.Length is 0 && (scopes.Length is not 0 || roles.Length is not 0))
        {
            throw Refuse(ErrorMessages.ClientAudienceRequired, id);
        }

        var client = new IdentityClient(
            id,
            displayName ?? id.Value,
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
                        id,
                        permission.Name,
                        permission.Resource),
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
    /// Returns a registration this type validated: the one given when it is already an
    /// <see cref="IdentityClient"/>, or a validated copy of another implementation.
    /// </summary>
    /// <exception cref="ArgumentException">The other implementation's contents break a rule.</exception>
    internal static IdentityClient From(IIdentityClient client)
    {
        return client as IdentityClient ?? Create(IdentityClientDescriptor.From(client));
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

    private static T[] Snapshot<T>(
        IdentityClientId id,
        IList<T> source,
        string listName,
        int maxCount,
        Func<T, string?> nameOf,
        string duplicateMessage)
    {
        if (source.Count > maxCount)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientListTooLong, id, listName, maxCount),
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
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientListEntryRequired, id, listName),
                    "descriptor");
            }

            if (!names.Add(name))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, duplicateMessage, id, name),
                    "descriptor");
            }

            snapshot[index] = entry;
        }

        return snapshot;
    }

    private static ArgumentException Refuse(string message, IdentityClientId id)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, message, id), "descriptor");
    }
}
