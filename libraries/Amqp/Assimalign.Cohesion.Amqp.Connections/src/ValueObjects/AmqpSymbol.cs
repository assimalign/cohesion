namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an AMQP 1.0 symbol containing seven-bit ASCII text. The empty string is valid.
/// </summary>
/// <remarks>
/// AMQP 1.0 Part 1, section 1.6.21 defines symbols as ASCII. Generic symbols have no
/// case-folding rule, so identity and ordering are ordinal and preserve the original spelling.
/// A default symbol holds no value; equality and hashing remain safe for it.
/// </remarks>
public readonly partial struct AmqpSymbol
{
    /// <summary>
    /// Determines whether a value is non-null seven-bit ASCII text.
    /// </summary>
    /// <param name="value">The candidate symbol.</param>
    /// <param name="message">The rejection reason, or null for a valid symbol.</param>
    /// <returns>True when the value is valid AMQP symbol text; otherwise false.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is not null)
        {
            foreach (char character in value)
            {
                if (character > 0x7f)
                {
                    message = ErrorMessages.SymbolInvalid;
                    return false;
                }
            }

            message = null;
            return true;
        }

        message = ErrorMessages.SymbolInvalid;
        return false;
    }
}
