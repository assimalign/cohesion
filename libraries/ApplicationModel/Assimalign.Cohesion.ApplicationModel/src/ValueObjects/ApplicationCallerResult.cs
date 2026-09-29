using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The result of one <see cref="IApplicationCallerAuthenticator.AuthenticateAsync"/> call.
/// </summary>
/// <param name="Status">The authentication outcome.</param>
/// <param name="Caller">
/// The mapped caller when <paramref name="Status"/> is <see cref="ApplicationCallerStatus.Authenticated"/>;
/// otherwise <see langword="null"/>. An authenticated result must carry its caller, because the
/// gateway applies its area and command checks to that identity.
/// </param>
/// <param name="Failure">
/// The reason for an <see cref="ApplicationCallerStatus.Unauthorized"/> or
/// <see cref="ApplicationCallerStatus.Forbidden"/> outcome; otherwise <see langword="null"/>.
/// </param>
/// <exception cref="ArgumentException">
/// <paramref name="Status"/> is <see cref="ApplicationCallerStatus.Authenticated"/> and
/// <paramref name="Caller"/> is <see langword="null"/>.
/// </exception>
public readonly record struct ApplicationCallerResult(
    ApplicationCallerStatus Status,
    ApplicationCaller? Caller,
    string? Failure)
{
    /// <summary>
    /// Gets the mapped caller when <see cref="Status"/> is
    /// <see cref="ApplicationCallerStatus.Authenticated"/>; otherwise <see langword="null"/>.
    /// </summary>
    public ApplicationCaller? Caller { get; init; } =
        Status == ApplicationCallerStatus.Authenticated && Caller is null
            ? throw new ArgumentException(
                "An Authenticated caller result must carry the authenticated ApplicationCaller.",
                nameof(Caller))
            : Caller;
}
