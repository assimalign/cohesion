namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An encoding in the type-system corpus and what the codec must do with it.
/// </summary>
/// <param name="Name">A short description of the case, unique in the corpus.</param>
/// <param name="Type">The name of the type the case exercises.</param>
/// <param name="Hex">The encoding, in hexadecimal; empty for a value the encoder refuses.</param>
/// <param name="Value">The value the encoding stands for, or the value the encoder refuses.</param>
/// <param name="Direction">Which way the case runs.</param>
/// <param name="Refusal">The message the codec must fail with, for a refused case; otherwise <see langword="null"/>.</param>
internal sealed record AmqpTypeEncodingCase(string Name, string Type, string Hex, object? Value, AmqpTypeEncodingDirection Direction, string? Refusal = null);

/// <summary>
/// Which way a case in the type-system corpus runs.
/// </summary>
internal enum AmqpTypeEncodingDirection
{
    /// <summary>
    /// The encoder writes the value as exactly this encoding, and the encoding decodes to the value.
    /// </summary>
    RoundTrip,

    /// <summary>
    /// The encoding decodes to the value, but the encoder writes the value another way: a wider form a peer
    /// may send, or a value the codec has no CLR array type for.
    /// </summary>
    DecodeOnly,

    /// <summary>
    /// The encoder writes the value as this encoding, which decodes to the type's own CLR type.
    /// </summary>
    EncodeOnly,

    /// <summary>
    /// The encoding is malformed, and decoding refuses it with <c>amqp:decode-error</c>.
    /// </summary>
    DecodeRefused,

    /// <summary>
    /// The value has no encoding, and the encoder refuses it with <see cref="System.ArgumentException"/>.
    /// </summary>
    EncodeRefused
}
