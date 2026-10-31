using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names an action a resource exposes, such as <c>log-level</c> or <c>flush-cache</c>.
/// </summary>
/// <remarks>
/// An action name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters, so it can stand as one segment of any transport's route.
/// </remarks>
public readonly partial struct ActionName
{
    /// <summary>Validates the action name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = NameGrammar.IsHyphenatedWords(value, 64) ? null : ErrorMessages.ActionNameInvalid;
        return message is null;
    }
}
