using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

public sealed partial class EbmlWriter
{
    internal async ValueTask WriteElementAsync(EbmlElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_baseDepth == 0 && _masters.Count == 0)
        {
            _crcCache.Clear();
        }

        var length = DataLength(element, _baseDepth + _masters.Count);
        var width = SizeWidth(element, length);
        if (element.Type == EbmlElementType.MasterElement)
        {
            if (_baseDepth + _masters.Count >= _maxDepth)
            {
                throw new InvalidOperationException(ErrorMessages.DepthLimit);
            }

            if (element.IsUnknownSize && _schema.Find(element.Identifier)?.UnknownSizeAllowed != true)
            {
                throw new EbmlDataFormatException(ErrorMessages.UnknownSize, ContentFormatErrorCode.InvalidStructure, _position);
            }

            await WriteHeaderAsync(element.Identifier, element.IsUnknownSize ? null : length, width, cancellationToken).ConfigureAwait(false);
            _masters.Add(new MasterFrame(_position, element.IsUnknownSize ? null : length, 0, width, false));
            var first = 0;
            if (RecalculateCrc && HasCrc(element))
            {
                if (!_crcCache.TryGetValue(element, out var crc))
                {
                    using var checksum = new EbmlCrcStream();
                    using var calculator = new EbmlWriter(checksum, _schema, true, _maxDepth)
                    { RecalculateCrc = true, _baseDepth = _baseDepth + _masters.Count, _crcCache = _crcCache };
                    for (var i = 1; i < element.Children.Count; i++)
                    {
                        await calculator.WriteElementAsync(element.Children[i], cancellationToken).ConfigureAwait(false);
                    }

                    crc = checksum.Value;
                    _crcCache.Add(element, crc);
                }

                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, crc);
                await WriteSegmentAsync(element.Children[0].Identifier, BinarySegment.Create(bytes),
                    element.Children[0].SizeLength, cancellationToken).ConfigureAwait(false);
                first = 1;
            }
            for (var i = first; i < element.Children.Count; i++)
            {
                await WriteElementAsync(element.Children[i], cancellationToken).ConfigureAwait(false);
            }

            EndMaster();
        }
        else if (element.RawPayload is BinarySegment raw)
        {
            await WriteSegmentAsync(element.Identifier, raw, width, cancellationToken).ConfigureAwait(false);
        }
        else if (element.Value is BinarySegment binary)
        {
            await WriteSegmentAsync(element.Identifier, binary, width, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var count = EbmlValueCodec.GetWireLength(_schema.Find(element.Identifier), element.Type, element.Value!);
            ValidateLeaf(element.Identifier, element.Type, element.Value!, count);
            await WriteHeaderAsync(element.Identifier, count, width, cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, count));
            try
            {
                EbmlValueCodec.Encode(element.Type, element.Value!, buffer.AsSpan(0, count));
                EnsureCount(count);
                await _output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                _position += count;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    private async ValueTask WriteSegmentAsync(VInt id, BinarySegment segment, int sizeLength, CancellationToken cancellationToken)
    {
        await WriteHeaderAsync(id, segment.Length, sizeLength, cancellationToken).ConfigureAwait(false);
        await segment.CopyToAsync(_output, cancellationToken).ConfigureAwait(false);
        _position += segment.Length;
    }

    private async ValueTask WriteHeaderAsync(VInt identifier, long? size, int sizeLength, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!identifier.IsValidIdentifier)
        {
            throw new ArgumentException(ErrorMessages.InvalidVInt, nameof(identifier));
        }

        var encodedSize = size is long length ? VInt.EncodeSize((ulong)length, sizeLength) : VInt.UnknownSize(sizeLength);
        var count = identifier.Length + encodedSize.Length;
        EnsureCount(count + (size ?? 0));
        var buffer = ArrayPool<byte>.Shared.Rent(16);
        try
        {
            EncodeVInt(identifier, buffer.AsSpan(0, identifier.Length));
            EncodeVInt(encodedSize, buffer.AsSpan(identifier.Length, encodedSize.Length));
            await _output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            _position += count;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void EncodeVInt(VInt value, Span<byte> destination)
    {
        var encoded = value.EncodedValue;
        for (var i = destination.Length - 1; i >= 0; i--, encoded >>= 8)
        {
            destination[i] = (byte)encoded;
        }
    }
}
