using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names the platform an infrastructure provider deploys onto, such as <c>kubernetes</c>: the
/// condition under which a resource's platform extensions apply.
/// </summary>
/// <remarks>
/// A platform name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters.
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
