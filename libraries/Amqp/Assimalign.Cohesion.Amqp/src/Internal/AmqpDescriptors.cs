using System;

namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The rule for the descriptor of a described type (AMQP 1.0 Part 1, section 1.5): a symbol or an unsigned
/// long, which the described values and arrays of the model share.
/// </summary>
internal static class AmqpDescriptors
{
    /// <summary>
    /// Checks a descriptor and returns it as an <see cref="AmqpSymbol"/> or a <see cref="ulong"/>, converting a
    /// <see cref="string"/> to an <see cref="AmqpSymbol"/>.
    /// </summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <param name="parameterName">The name of the parameter that carries it, for the exception.</param>
    /// <returns>The descriptor as an <see cref="AmqpSymbol"/> or a <see cref="ulong"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="descriptor"/> is neither a symbol nor an unsigned long, is a default symbol, which
    /// holds no name, or is a string that is not seven-bit ASCII.
    /// </exception>
    public static object Normalize(object descriptor, string parameterName)
    {
        return descriptor switch
        {
            AmqpSymbol { Value: not null } symbol => symbol,
            ulong code => code,
            string name => AmqpSymbol.TryCreate(name, out AmqpSymbol named)
                ? named
                : throw new ArgumentException(ErrorMessages.SymbolInvalid, parameterName),
            _ => throw new ArgumentException(ErrorMessages.DescriptorTypeInvalid, parameterName)
        };
    }
}
