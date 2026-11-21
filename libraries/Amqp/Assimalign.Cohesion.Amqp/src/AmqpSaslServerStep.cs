using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the result of one server-side SASL evaluation: a challenge for the client, or the outcome
/// that completes the exchange (AMQP 1.0 Part 5, sections 5.3.3.3 and 5.3.3.6).
/// </summary>
public sealed class AmqpSaslServerStep
{
    private AmqpSaslServerStep(AmqpSaslCode? code, ReadOnlyMemory<byte> data, string? identity)
    {
        Code = code;
        Data = data;
        Identity = identity;
    }

    /// <summary>
    /// Gets a value indicating whether the step completes the exchange.
    /// </summary>
    public bool IsComplete => Code is not null;

    /// <summary>
    /// Gets the outcome code, or <see langword="null"/> for a challenge.
    /// </summary>
    public AmqpSaslCode? Code { get; }

    /// <summary>
    /// Gets the challenge data, or the outcome's additional data.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Gets the identity a successful outcome authenticated, or <see langword="null"/>.
    /// </summary>
    public string? Identity { get; }

    /// <summary>
    /// Creates a step that sends the client a challenge.
    /// </summary>
    /// <param name="challenge">The challenge data.</param>
    /// <returns>The challenge step.</returns>
    public static AmqpSaslServerStep Challenge(ReadOnlyMemory<byte> challenge)
    {
        return new AmqpSaslServerStep(null, challenge, null);
    }

    /// <summary>
    /// Creates a step that accepts the client.
    /// </summary>
    /// <param name="identity">The identity the exchange authenticated, or <see langword="null"/> for an anonymous client.</param>
    /// <param name="additionalData">The additional data the outcome carries, such as a server signature.</param>
    /// <returns>The successful outcome.</returns>
    public static AmqpSaslServerStep Success(string? identity = null, ReadOnlyMemory<byte> additionalData = default)
    {
        return new AmqpSaslServerStep(AmqpSaslCode.Ok, additionalData, identity);
    }

    /// <summary>
    /// Creates a step that refuses the client.
    /// </summary>
    /// <param name="code">The failure code. Defaults to <see cref="AmqpSaslCode.Auth"/>, a failure of the supplied credentials.</param>
    /// <returns>The failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="code"/> is <see cref="AmqpSaslCode.Ok"/> or not a defined code.</exception>
    public static AmqpSaslServerStep Fail(AmqpSaslCode code = AmqpSaslCode.Auth)
    {
        if (code is AmqpSaslCode.Ok || !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        return new AmqpSaslServerStep(code, default, null);
    }
}
