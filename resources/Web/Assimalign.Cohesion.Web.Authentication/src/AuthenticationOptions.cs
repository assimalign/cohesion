using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The builder-time registry of authentication schemes plus the default-scheme selections that
/// decide which scheme runs when a caller does not name one explicitly.
/// </summary>
/// <remarks>
/// <para>
/// This type is populated at composition time (a <c>*.Hosting</c> project): the root
/// <c>AddAuthentication</c> call sets the default-scheme names, and each <c>AddCookie</c> /
/// <c>AddJwtBearer</c> call registers a <see cref="AuthenticationScheme"/>. The
/// <see cref="IAuthenticationService"/> reads it live per request, so schemes registered after
/// the options are first handed out are still visible.
/// </para>
/// <para>
/// Each specific default (<see cref="DefaultAuthenticateScheme"/> and friends) falls back to
/// <see cref="DefaultScheme"/> when it is not set, mirroring the ASP.NET Core default-scheme
/// resolution rules.
/// </para>
/// </remarks>
public sealed class AuthenticationOptions
{
    private readonly Dictionary<string, AuthenticationScheme> _schemes = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the fallback scheme used by every verb when its specific default is unset.
    /// </summary>
    public string? DefaultScheme { get; set; }

    /// <summary>
    /// Gets or sets the scheme used by <c>AuthenticateAsync</c> when no scheme is named. Falls
    /// back to <see cref="DefaultScheme"/>.
    /// </summary>
    public string? DefaultAuthenticateScheme { get; set; }

    /// <summary>
    /// Gets or sets the scheme used by <c>ChallengeAsync</c> when no scheme is named. Falls back
    /// to <see cref="DefaultScheme"/>.
    /// </summary>
    public string? DefaultChallengeScheme { get; set; }

    /// <summary>
    /// Gets or sets the scheme used by <c>ForbidAsync</c> when no scheme is named. Falls back to
    /// <see cref="DefaultChallengeScheme"/> then <see cref="DefaultScheme"/>.
    /// </summary>
    public string? DefaultForbidScheme { get; set; }

    /// <summary>
    /// Gets or sets the scheme used by <c>SignInAsync</c> when no scheme is named. Falls back to
    /// <see cref="DefaultScheme"/>.
    /// </summary>
    public string? DefaultSignInScheme { get; set; }

    /// <summary>
    /// Gets or sets the scheme used by <c>SignOutAsync</c> when no scheme is named. Falls back to
    /// <see cref="DefaultSignInScheme"/> then <see cref="DefaultScheme"/>.
    /// </summary>
    public string? DefaultSignOutScheme { get; set; }

    /// <summary>
    /// Gets the registered schemes, in registration order.
    /// </summary>
    public IReadOnlyCollection<AuthenticationScheme> Schemes => _schemes.Values;

    /// <summary>
    /// Registers a scheme.
    /// </summary>
    /// <param name="scheme">The scheme to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A scheme with the same name is already registered.</exception>
    public void AddScheme(AuthenticationScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        if (!_schemes.TryAdd(scheme.Name, scheme))
        {
            throw new InvalidOperationException($"Scheme '{scheme.Name}' has already been registered.");
        }
    }

    /// <summary>
    /// Looks up a registered scheme by name.
    /// </summary>
    /// <param name="name">The scheme name.</param>
    /// <returns>The scheme, or <see langword="null"/> when no scheme with that name is registered.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
    public AuthenticationScheme? GetScheme(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _schemes.TryGetValue(name, out var scheme) ? scheme : null;
    }

    /// <summary>
    /// Resolves the effective default scheme for <c>AuthenticateAsync</c>.
    /// </summary>
    /// <returns>The scheme name, or <see langword="null"/> when none is configured.</returns>
    public string? ResolveDefaultAuthenticateScheme() => DefaultAuthenticateScheme ?? DefaultScheme;

    /// <summary>
    /// Resolves the effective default scheme for <c>ChallengeAsync</c>.
    /// </summary>
    /// <returns>The scheme name, or <see langword="null"/> when none is configured.</returns>
    public string? ResolveDefaultChallengeScheme() => DefaultChallengeScheme ?? DefaultScheme;

    /// <summary>
    /// Resolves the effective default scheme for <c>ForbidAsync</c>.
    /// </summary>
    /// <returns>The scheme name, or <see langword="null"/> when none is configured.</returns>
    public string? ResolveDefaultForbidScheme() => DefaultForbidScheme ?? DefaultChallengeScheme ?? DefaultScheme;

    /// <summary>
    /// Resolves the effective default scheme for <c>SignInAsync</c>.
    /// </summary>
    /// <returns>The scheme name, or <see langword="null"/> when none is configured.</returns>
    public string? ResolveDefaultSignInScheme() => DefaultSignInScheme ?? DefaultScheme;

    /// <summary>
    /// Resolves the effective default scheme for <c>SignOutAsync</c>.
    /// </summary>
    /// <returns>The scheme name, or <see langword="null"/> when none is configured.</returns>
    public string? ResolveDefaultSignOutScheme() => DefaultSignOutScheme ?? DefaultSignInScheme ?? DefaultScheme;
}
