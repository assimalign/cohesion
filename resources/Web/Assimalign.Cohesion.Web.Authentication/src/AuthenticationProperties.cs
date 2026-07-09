using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Carries the state that travels alongside a <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// through an authentication flow: redirect targets, persistence and lifetime hints, and an
/// open-ended <see cref="Items"/> bag for scheme-specific data.
/// </summary>
/// <remarks>
/// <para>
/// The typed members (<see cref="IsPersistent"/>, <see cref="RedirectUri"/>,
/// <see cref="IssuedUtc"/>, <see cref="ExpiresUtc"/>, <see cref="AllowRefresh"/>) mirror the
/// well-known ASP.NET Core <c>AuthenticationProperties</c> shape so handlers that persist a
/// ticket (the cookie handler) and handlers that never do (the bearer handler) speak one
/// vocabulary. Values a handler does not understand ride along untouched in <see cref="Items"/>.
/// </para>
/// <para>
/// The type is a mutable bag by design: a handler reads it on sign-in, may refresh
/// <see cref="ExpiresUtc"/> during a sliding-expiration renewal, and hands it back on the
/// authenticated ticket.
/// </para>
/// </remarks>
public sealed class AuthenticationProperties
{
    /// <summary>
    /// Initializes empty properties.
    /// </summary>
    public AuthenticationProperties()
        : this(new Dictionary<string, string?>(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Initializes properties over the supplied backing dictionary. The dictionary is used
    /// as-is (not copied), so callers that want isolation should pass a fresh instance.
    /// </summary>
    /// <param name="items">The backing item store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public AuthenticationProperties(IDictionary<string, string?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>
    /// Gets the open-ended, string-keyed store for scheme-specific data that is preserved
    /// across the flow. Never <see langword="null"/>.
    /// </summary>
    public IDictionary<string, string?> Items { get; }

    /// <summary>
    /// Gets or sets whether the authentication session should persist across browser sessions
    /// (for the cookie handler, whether the cookie is a persistent cookie). <see langword="null"/>
    /// leaves the handler's default in effect.
    /// </summary>
    public bool? IsPersistent { get; set; }

    /// <summary>
    /// Gets or sets the full path or absolute URI to redirect to during a challenge or sign-out.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Gets or sets the instant the authentication ticket was issued.
    /// </summary>
    public DateTimeOffset? IssuedUtc { get; set; }

    /// <summary>
    /// Gets or sets the instant the authentication ticket expires. The cookie handler treats a
    /// past value as an expired ticket and refuses it.
    /// </summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>
    /// Gets or sets whether the ticket may be refreshed by a sliding-expiration renewal.
    /// <see langword="null"/> leaves the handler's default in effect.
    /// </summary>
    public bool? AllowRefresh { get; set; }

    /// <summary>
    /// Creates a deep copy of these properties, including a fresh copy of <see cref="Items"/>.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public AuthenticationProperties Clone()
    {
        return new AuthenticationProperties(new Dictionary<string, string?>(Items, StringComparer.Ordinal))
        {
            IsPersistent = IsPersistent,
            RedirectUri = RedirectUri,
            IssuedUtc = IssuedUtc,
            ExpiresUtc = ExpiresUtc,
            AllowRefresh = AllowRefresh,
        };
    }
}
