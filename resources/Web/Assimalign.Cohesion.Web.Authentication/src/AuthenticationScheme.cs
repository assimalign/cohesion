using System;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// A named authentication scheme: a stable name, an optional human-readable display name, and
/// the factory that produces the per-request <see cref="IAuthenticationHandler"/> that runs the
/// scheme's authenticate/challenge/forbid (and, for sign-in schemes, sign-in/sign-out) verbs.
/// </summary>
/// <remarks>
/// <para>
/// The handler is produced through a <see cref="Func{TResult}"/> captured at builder time rather
/// than through reflection or a DI container. This keeps scheme resolution allocation-cheap and
/// fully AOT-safe: the factory closes over the scheme's already-constructed options (and, for the
/// cookie scheme, its ticket protector), so no runtime type activation is ever needed.
/// </para>
/// <para>
/// A scheme is a builder-time value. It is registered into <see cref="AuthenticationOptions"/>
/// by the composition root (a <c>*.Hosting</c> project) and never mutated afterward.
/// </para>
/// </remarks>
public sealed class AuthenticationScheme
{
    private readonly Func<IAuthenticationHandler> _handlerFactory;

    /// <summary>
    /// Initializes a new scheme.
    /// </summary>
    /// <param name="name">The stable scheme name (for example <c>"Cookies"</c> or <c>"Bearer"</c>).</param>
    /// <param name="displayName">An optional human-readable name; defaults to <paramref name="name"/> when omitted.</param>
    /// <param name="handlerFactory">The factory that creates a fresh handler for a request.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="handlerFactory"/> is <see langword="null"/>.</exception>
    public AuthenticationScheme(string name, string? displayName, Func<IAuthenticationHandler> handlerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        Name = name;
        DisplayName = displayName ?? name;
        _handlerFactory = handlerFactory;
    }

    /// <summary>
    /// Gets the stable scheme name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the human-readable display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Creates a fresh handler instance for the current request.
    /// </summary>
    /// <returns>A new <see cref="IAuthenticationHandler"/>.</returns>
    /// <exception cref="InvalidOperationException">The factory returned <see langword="null"/>.</exception>
    public IAuthenticationHandler CreateHandler()
        => _handlerFactory() ?? throw new InvalidOperationException(
            $"The handler factory for scheme '{Name}' returned null.");
}
