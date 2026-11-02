using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a platform, such as <c>kubernetes</c> or <c>inprocess</c>: what a gateway deploys onto, the
/// condition under which a resource's platform extensions apply, and how a resource instance picks
/// the client that reaches its gateway.
/// </summary>
/// <remarks>
/// <para>
/// A platform name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters.
/// </para>
/// <para>
/// A platform comes in two halves that share its name: the gateway half deploys onto the platform,
/// and the instance half reaches the gateway from an instance the platform runs. A gateway hands
/// every instance it starts the platform's name (<see cref="AppEnvironment.Variables.Gateway"/>).
/// </para>
/// </remarks>
public readonly partial struct PlatformName
{
    /// <summary>Validates the platform name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = NameGrammar.IsHyphenatedWords(value, 64) ? null : ErrorMessages.PlatformNameInvalid;
        return message is null;
    }
}
