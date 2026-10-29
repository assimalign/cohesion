using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names one platform extension of a resource, such as <c>autoscaler</c>, unique among the
/// resource's extensions for the platform.
/// </summary>
/// <remarks>
/// An extension name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters. The platform's package defines which extensions it has.
/// </remarks>
public readonly partial struct ExtensionName
{
    /// <summary>Validates the extension name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = NameGrammar.IsHyphenatedWords(value, 64) ? null : ErrorMessages.ExtensionNameInvalid;
        return message is null;
    }
}
