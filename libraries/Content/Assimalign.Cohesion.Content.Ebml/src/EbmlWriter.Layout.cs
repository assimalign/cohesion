using System;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

public sealed partial class EbmlWriter
{
    /// <summary>Measures an element's complete encoding without writing or reading payload bytes.</summary>
    /// <param name="element">The leaf or tree; preserved scalar bytes and retained size widths are included.</param>
    /// <param name="schema">The active schema, or the RFC header schema when null.</param>
    /// <param name="maxDepth">The maximum master depth, from one through 256.</param>
    /// <returns>The combined ID, size and payload length, including the available children of unknown-size masters.</returns>
    /// <remarks>Uses the same lengths as WriteElement. This is a layout query, not complete document validation.</remarks>
    /// <exception cref="ArgumentNullException">The element is null.</exception>
    /// <exception cref="ArgumentException">An identifier, value, or length cannot be encoded.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is invalid.</exception>
    /// <exception cref="InvalidOperationException">The tree exceeds the depth limit.</exception>
    public static long GetEncodedLength(EbmlElement element, EbmlSchema? schema = null, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, 256);
        var size = GetDataLength(element, schema ?? EbmlSchema.Header, 0, maxDepth);
        var headerLength = element.Identifier.Length + SizeWidth(element, size);
        if (size > long.MaxValue - headerLength)
        {
            throw new ArgumentException(ErrorMessages.InvalidSize);
        }
        return size + headerLength;
    }

    /// <summary>Measures a typed leaf's complete encoding without writing or reading its payload.</summary>
    /// <param name="identifier">The encoded ID, authorized by the schema or the generic ID rule.</param>
    /// <param name="type">The leaf type.</param>
    /// <param name="value">The ulong, long, double, string or BinarySegment value.</param>
    /// <param name="schema">The active schema, or the RFC header schema when null.</param>
    /// <param name="length">The scalar payload width, or -1 for minimal/default encoding. Binary requires -1.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <returns>The combined ID, size and payload length.</returns>
    /// <exception cref="ArgumentException">The identifier, type, value or explicit width is invalid.</exception>
    /// <exception cref="ContentFormatException">The leaf violates its schema type, range or length constraints.</exception>
    public static long GetEncodedLength(VInt identifier, EbmlElementType type, object value,
        EbmlSchema? schema = null, int length = -1, int sizeLength = 0)
    {
        schema ??= EbmlSchema.Header;
        var definition = schema.Find(identifier);
        if (!identifier.IsValidIdentifier && definition is null)
        {
            throw new ArgumentException(ErrorMessages.InvalidVInt, nameof(identifier));
        }
        long size;
        if (type == EbmlElementType.Binary && value is BinarySegment binary && length == -1)
        {
            size = binary.Length;
        }
        else
        {
            size = EbmlValueCodec.GetWireLength(definition, type, value, length);
        }
        if (definition is not null && (definition.Type != type || !definition.Contains(value) ||
            size < definition.MinimumLength || size > definition.MaximumLength))
        {
            throw new EbmlDataFormatException(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidValue, 0);
        }
        return identifier.Length + VInt.EncodeSize((ulong)size, sizeLength).Length + size;
    }

    /// <summary>Copies one complete original element, including its original header, from a source segment.</summary>
    /// <param name="source">The exact original element bytes, borrowed for this call.</param>
    /// <remarks>Checks the outer ID, size and schema type/length before writing. Children and scalar contents
    /// are copied verbatim without semantic validation or CRC recalculation. An unknown-size master must
    /// have a schema definition permitting unknown size; the segment supplies its original extent.
    /// Source and destination must refer to different storage. No payload-sized buffer is allocated.</remarks>
    /// <exception cref="ContentFormatException">The original header is invalid, truncated, or disagrees with its range or schema.</exception>
    /// <exception cref="InvalidOperationException">The bytes exceed an open master's declared size.</exception>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    public void WriteRawElement(BinarySegment source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (var input = source.OpenRead())
        {
            var origin = input is ContentSliceStream slice ? slice.SourceOffset : 0;
            var identifier = VInt.Read(input, 8, null);
            var definition = _schema.Find(identifier);
            if (!identifier.IsValidIdentifier && definition is null)
            {
                throw new EbmlDataFormatException(ErrorMessages.InvalidVInt, ContentFormatErrorCode.InvalidStructure, origin);
            }
            var size = VInt.Read(input, 8, null);
            var payloadLength = source.Length - identifier.Length - size.Length;
            if (!size.IsReserved && (long)size.Value != payloadLength)
            {
                throw new EbmlDataFormatException(ErrorMessages.InvalidSize, ContentFormatErrorCode.InvalidStructure, origin);
            }
            if (size.IsReserved && definition?.UnknownSizeAllowed != true)
            {
                throw new EbmlDataFormatException(ErrorMessages.UnknownSize, ContentFormatErrorCode.InvalidStructure, origin);
            }
            if (definition is not null && (!EbmlValueCodec.ValidLength(definition.Type, payloadLength) ||
                payloadLength < definition.MinimumLength || payloadLength > definition.MaximumLength))
            {
                throw new EbmlDataFormatException(ErrorMessages.InvalidSize, ContentFormatErrorCode.InvalidValue, origin);
            }
        }
        EnsureCount(source.Length);
        source.CopyTo(_output);
        _position += source.Length;
    }
}
