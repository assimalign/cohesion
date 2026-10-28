namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a value a resource has only once it is deployed, such as <c>endpoint</c>, the address its
/// platform gives it.
/// </summary>
/// <remarks>
/// An output name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters. The resource's infrastructure provider defines which outputs a resource has.
/// </remarks>
public readonly partial struct OutputName
{
    /// <summary>
    /// Gets the name of the address a resource's platform gives it, such as a load balancer's or a
    /// service's, through which its dependents reach every instance.
    /// </summary>
    public static OutputName Endpoint => "endpoint";

    /// <summary>Validates the output name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.OutputNameInvalid;
        if (value is null || value.Length is 0 or > 64)
        {
            return false;
        }

        // Each word is a non-empty run of letters and digits; a hyphen joins words, so it may
        // neither lead, trail, nor repeat.
        bool wordStart = true;
        foreach (char character in value)
        {
            if (character == '-')
            {
                if (wordStart)
                {
                    return false;
                }

                wordStart = true;
            }
            else if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                wordStart = false;
            }
            else
            {
                return false;
            }
        }

        if (wordStart)
        {
            return false;
        }

        message = null;
        return true;
    }
}
