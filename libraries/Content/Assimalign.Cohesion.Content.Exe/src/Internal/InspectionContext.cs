using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Internal;

internal sealed class InspectionContext : IDisposable
{
    private readonly ContentSliceStream _source;
    private readonly ExecutableReaderOptions _options;
    private readonly bool _asynchronous;
    private readonly byte[] _scratch;
    private int _remainingBudget;

    internal InspectionContext(Stream stream, ExecutableReaderOptions options, bool asynchronous)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var origin = stream is ContentSliceStream slice ? slice.SourceOffset : 0;
        var position = stream.CanSeek || stream is ContentSliceStream ? stream.Position : 0;
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ContentFormatException(ErrorMessages.SeekableSourceRequired, ContentFormatErrorCode.Unsupported, origin + position);
        }
        if (position < 0 || position > stream.Length || origin > long.MaxValue - stream.Length)
        {
            throw new ContentFormatException(ErrorMessages.InvalidRange, ContentFormatErrorCode.InvalidStructure, origin);
        }
        _source = ContentSliceStream.Create(stream, position, stream.Length - position);
        _options = options;
        _remainingBudget = options.MaxBufferedBytes;
        _asynchronous = asynchronous;
        _scratch = ArrayPool<byte>.Shared.Rent(128);
    }

    internal long Origin => _source.SourceOffset;
    internal ulong Length => (ulong)_source.Length;

    internal void CheckRange(ulong offset, ulong size, ulong fieldOffset)
    {
        if (offset > Length || size > Length - offset)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedRange, ContentFormatErrorCode.Truncated, Origin + (long)fieldOffset);
        }
    }

    internal void Reserve(ulong bytes, ulong fieldOffset)
    {
        if (bytes > (ulong)_remainingBudget)
        {
            throw new ContentFormatException(ErrorMessages.MetadataLimit, ContentFormatErrorCode.LimitExceeded, Origin + (long)fieldOffset);
        }
        _remainingBudget -= (int)bytes;
    }

    internal int CheckTable(ulong offset, ulong count, uint stride, uint minimumStride, ulong fieldOffset)
    {
        if (count > (uint)_options.MaxTableEntries)
        {
            throw new ContentFormatException(ErrorMessages.TableLimit, ContentFormatErrorCode.LimitExceeded, Origin + (long)fieldOffset);
        }
        if (count == 0)
        {
            return 0;
        }
        if (offset == 0 || stride < minimumStride)
        {
            throw new ContentFormatException(ErrorMessages.InvalidTable, ContentFormatErrorCode.InvalidStructure, Origin + (long)fieldOffset);
        }
        // Count and stride are bounded 32-bit quantities before this multiplication.
        var bytes = count * stride;
        CheckRange(offset, bytes, fieldOffset);
        Reserve(bytes, fieldOffset);
        return (int)count;
    }

    internal async ValueTask<ReadOnlyMemory<byte>> ReadAtAsync(ulong offset, int count, CancellationToken cancellationToken = default)
    {
        var memory = _scratch.AsMemory(0, count);
        await ReadIntoAsync(offset, memory, cancellationToken).ConfigureAwait(false);
        return memory;
    }

    internal async ValueTask ReadIntoAsync(ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckRange(offset, (ulong)buffer.Length, Math.Min(offset, Length));
        _source.Position = (long)offset;
        if (_asynchronous)
        {
            await _source.ReadContentExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _source.ReadContentExactly(buffer.Span);
        }
    }

    internal string ReadName(ReadOnlySpan<byte> table, uint offset, ulong tableOffset, ulong fieldOffset)
    {
        if (offset >= (uint)table.Length)
        {
            throw new ContentFormatException(ErrorMessages.InvalidName, ContentFormatErrorCode.InvalidValue, Origin + (long)fieldOffset);
        }
        var name = table[(int)offset..];
        var end = name.IndexOf((byte)0);
        if (end < 0)
        {
            throw new ContentFormatException(ErrorMessages.UnterminatedString, ContentFormatErrorCode.InvalidStructure, Origin + (long)tableOffset + offset);
        }
        Reserve((ulong)end * 2, fieldOffset);
        return Encoding.UTF8.GetString(name[..end]);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_scratch);
        _source.Dispose();
    }
}
