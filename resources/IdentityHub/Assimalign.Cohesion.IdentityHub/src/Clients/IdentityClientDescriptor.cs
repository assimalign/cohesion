using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Describes the contents of an app registration before it is materialized into an immutable
/// <see cref="IdentityClient"/>. It is how a registration is written in code, and how a stored
/// one is changed.
/// </summary>
/// <remarks>
/// <para>
/// A descriptor validates nothing by itself: <see cref="IdentityClient.Create(IdentityClientDescriptor)"/>
/// checks the whole registration at once and snapshots it, so changing the descriptor
/// afterwards changes no registration. The parts that carry a grammar of their own, such as a
/// redirect or a credential, are validated when they are created.
/// </para>
/// <para>
/// To change a stored registration, start from <see cref="From"/>, edit, create the
/// replacement, and store it with <see cref="IIdentityClientRegistry.TryReplaceAsync"/> under
/// the version that was read.
/// </para>
/// </remarks>
public sealed class IdentityClientDescriptor
{
    /// <summary>
    /// Gets or sets the registration's identifier: the OAuth <c>client_id</c>. Required at
    /// materialization.
    /// </summary>
    public IdentityClientId Id { get; set; }

    /// <summary>
    /// Gets or sets the name a person is shown for the application: 1 to 128 characters with no
    /// control character. When it is <see langword="null"/>, the identifier is shown.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets what the application is, in words a person is shown: at most 256 characters.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the grants the application may use to obtain tokens. The default is
    /// <see cref="IdentityClientGrants.None"/>: a registration that only exposes an API.
    /// </summary>
    public IdentityClientGrants Grants { get; set; }

    /// <summary>
    /// Gets or sets how long an access token issued to the application is valid: greater than
    /// zero and at most <see cref="IdentityClient.MaxAccessTokenLifetime"/>. The default is
    /// <see cref="IdentityClient.DefaultAccessTokenLifetime"/>.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = IdentityClient.DefaultAccessTokenLifetime;

    /// <summary>
    /// Gets the URIs a user may be sent back to after the authorization code grant.
    /// </summary>
    public IList<IdentityClientRedirect> Redirects { get; } = new List<IdentityClientRedirect>();

    /// <summary>
    /// Gets what the application proves its identity with.
    /// </summary>
    public IList<IdentityClientCredential> Credentials { get; } = new List<IdentityClientCredential>();

    /// <summary>
    /// Gets the audiences of the API the registration exposes.
    /// </summary>
    public IList<IdentityClientAudience> Audiences { get; } = new List<IdentityClientAudience>();

    /// <summary>
    /// Gets the scopes of the API the registration exposes.
    /// </summary>
    public IList<IdentityClientScope> Scopes { get; } = new List<IdentityClientScope>();

    /// <summary>
    /// Gets the roles of the API the registration exposes.
    /// </summary>
    public IList<IdentityClientRole> Roles { get; } = new List<IdentityClientRole>();

    /// <summary>
    /// Gets the permissions the application holds on APIs.
    /// </summary>
    public IList<IdentityClientPermission> Permissions { get; } = new List<IdentityClientPermission>();

    /// <summary>
    /// Creates a descriptor that holds the contents of a registration, as the starting point of
    /// a replacement.
    /// </summary>
    /// <remarks>
    /// <see cref="IIdentityClient.IsLocked"/> and <see cref="IIdentityClient.Version"/> are not
    /// contents: a registry sets them, so they are not copied.
    /// </remarks>
    /// <param name="client">The registration to copy.</param>
    /// <returns>A descriptor the caller may change freely.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static IdentityClientDescriptor From(IIdentityClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var descriptor = new IdentityClientDescriptor
        {
            Id = client.Id,
            DisplayName = client.DisplayName,
            Description = client.Description,
            Grants = client.Grants,
            AccessTokenLifetime = client.AccessTokenLifetime,
        };

        Copy(client.Redirects, descriptor.Redirects);
        Copy(client.Credentials, descriptor.Credentials);
        Copy(client.Audiences, descriptor.Audiences);
        Copy(client.Scopes, descriptor.Scopes);
        Copy(client.Roles, descriptor.Roles);
        Copy(client.Permissions, descriptor.Permissions);
        return descriptor;
    }

    private static void Copy<T>(IReadOnlyList<T>? source, IList<T> destination)
    {
        if (source is null)
        {
            return;
        }

        for (int index = 0; index < source.Count; index++)
        {
            destination.Add(source[index]);
        }
    }
}
