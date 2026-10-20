using System;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>Encodes Graph composite values, exact Decimal bits and scalar DatabaseValueCodec components.</summary>
/// <remarks>This format requires GraphProtocol.CompositeValues. It is never an index key format.
/// Lists decode to object arrays; maps decode to read-only ordinal string dictionaries.</remarks>
public static class GraphValueCodec
{
    /// <summary>The maximum nested container depth (a root value has depth zero).</summary>
    public const int MaxDepth = GraphValueEncoding.MaxDepth;
    /// <summary>The maximum total values, including the root, in one encoded value.</summary>
    public const int MaxValueCount = GraphValueEncoding.MaxValueCount;
    /// <summary>The maximum encoded size of one value or row.</summary>
    public const int MaxPayloadLength = GraphValueEncoding.MaxPayloadLength;

    /// <summary>Encodes one scalar, list or map with explicit runtime type tags.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The complete encoded value.</returns>
    /// <exception cref="DatabaseTypeException">A type or format bound is unsupported.</exception>
    public static byte[] Encode(object? value) => GraphValueEncoding.Encode(value, Messages);

    /// <summary>Decodes one bounded composite component.</summary>
    /// <param name="payload">The complete component.</param>
    /// <returns>The decoded value with scalar runtime types preserved.</returns>
    /// <exception cref="ProtocolException">The component is malformed or exceeds a bound.</exception>
    public static object? Decode(ReadOnlySpan<byte> payload) => GraphValueEncoding.Decode(payload, Messages);

    private static GraphValueEncoding.Messages Messages => new(
        ErrorMessages.CompositeLimit, ErrorMessages.CompositeMalformed, ErrorMessages.CompositeRequired);
}