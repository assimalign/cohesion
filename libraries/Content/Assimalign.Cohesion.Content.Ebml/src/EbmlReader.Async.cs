using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

public sealed partial class EbmlReader
{
    internal async ValueTask<bool> ReadNextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ReaderState);
        }

        if (_atEnd)
        {
            return false;
        }

        var boundary = Boundary();
        if (_pending is not null || boundary is long end && _input.Position == end)
        {
            return ReadNext();
        }

        _input.End = boundary;
        _input.SuppressObservation = true;
        var start = _input.Position;
        try
        {
            if (!await _input.TryReadExactlyAsync(_headerBytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false))
            {
                if (boundary is long expected && _input.Position < expected)
                {
                    throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, _input.Position);
                }

                _atEnd = true;
                return false;
            }
            var id = await ReadVIntAsync(0, _options.MaxIdLength, cancellationToken).ConfigureAwait(false);
            if (!id.IsValidIdentifier && _schema.Find(id) is null)
            {
                throw new EbmlDataFormatException(ErrorMessages.InvalidVInt, ContentFormatErrorCode.InvalidStructure, start);
            }

            await _input.ReadContentExactlyAsync(_headerBytes.AsMemory(id.Length, 1), cancellationToken).ConfigureAwait(false);
            var size = await ReadVIntAsync(id.Length, _options.MaxSizeLength, cancellationToken).ConfigureAwait(false);
            _pending = new EbmlElementHeader(id, size, start, _schema.Find(id)?.Type ?? EbmlElementType.Binary);
        }
        catch (ContentFormatException error) when (error is not EbmlDataFormatException)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, error.ErrorCode, _input.Position, error);
        }
        return ReadNext(); // Validation and pending-sibling handling are shared with the synchronous reader.
    }

    private async ValueTask<VInt> ReadVIntAsync(int offset, int maximum, CancellationToken cancellationToken)
    {
        var length = VInt.GetLength(_headerBytes[offset], maximum, _input.Position - 1);
        await _input.ReadContentExactlyAsync(_headerBytes.AsMemory(offset + 1, length - 1), cancellationToken).ConfigureAwait(false);
        ulong encoded = 0;
        for (var i = offset; i < offset + length; i++)
        {
            encoded = (encoded << 8) | _headerBytes[i];
        }

        return VInt.FromEncoded(encoded);
    }

    internal async ValueTask<EbmlElement> ReadTreeAsync(int[] count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count[0] >= _options.MaxElementCount)
        {
            throw new EbmlDataFormatException(ErrorMessages.ElementLimit, ContentFormatErrorCode.LimitExceeded, Current.Offset);
        }
        count[0]++;

        var header = Current;
        var sourceOffset = _source.Position; // Document adapters require seekable input.
        EbmlElement element;
        if (header.Type == EbmlElementType.MasterElement)
        {
            element = EbmlElement.Create(header.Identifier, header.Type);
            EnterMaster();
            while (await ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                element.Children.Add(await ReadTreeAsync(count, cancellationToken).ConfigureAwait(false));
            }

            LeaveMaster();
        }
        else
        {
            var value = await ReadValueAsync(cancellationToken).ConfigureAwait(false);
            var type = EbmlValueCodec.Matches(header.Type, value) ? header.Type : EbmlElementType.Binary;
            element = EbmlElement.Create(header.Identifier, type, value);
            element.RawPayload = BinarySegment.Create(_source, sourceOffset, header.DataSize!.Value);
        }
        element.IsUnknownSize = header.IsUnknownSize;
        element.SizeLength = header.SizeLength;
        element.Offset = header.Offset;
        return element;
    }

    private async ValueTask<object> ReadValueAsync(CancellationToken cancellationToken)
    {
        var header = Current;
        var type = header.Type;
        var length = header.DataSize!.Value;
        if (!EbmlValueCodec.ValidLength(type, length))
        {
            type = EbmlElementType.Binary; // ReadNext already validated.
        }

        object value;
        try
        {
            if (header.Identifier.EncodedValue == 0xBF && _options.VerifyCrc32 && length == 4 && _masters.Count > 0)
            {
                var bytes = new byte[4];
                await _input.ReadContentExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                _masters[^1].ExpectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                _masters[^1].CrcOffset = header.DataOffset;
                value = BinarySegment.Create(bytes);
            }
            else if (type == EbmlElementType.Binary)
            {
                value = BinarySegment.Create(_source, _source.Position, length);
                if (!_options.VerifyCrc32 || _masters.Count == 0)
                {
                    SkipData(length);
                }
                else
                {
                    await _input.SkipExactlyAsync(length, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (length > _options.MaxBufferedValueLength)
                {
                    throw new EbmlDataFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, header.DataOffset);
                }

                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)length));
                try
                {
                    await _input.ReadContentExactlyAsync(buffer.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                    try { value = EbmlValueCodec.Decode(type, buffer.AsSpan(0, (int)length), header.DataOffset); }
                    catch (EbmlDataFormatException error) when (_options.Lenient && error.ErrorCode == ContentFormatErrorCode.InvalidValue)
                    {
                        _validation.Report(ErrorMessages.InvalidValue, error.ErrorCode, error.Position!.Value);
                        value = BinarySegment.Create(buffer.AsSpan(0, (int)length).ToArray());
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        catch (ContentFormatException error) when (error is not EbmlDataFormatException)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, error.ErrorCode, _input.Position, error);
        }
        return CompleteValue(header, value);
    }
}
