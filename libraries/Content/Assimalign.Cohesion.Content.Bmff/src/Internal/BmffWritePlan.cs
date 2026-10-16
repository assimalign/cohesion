using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

internal sealed class BmffWritePlan
{
    private readonly BmffBox _box;
    private readonly BmffWritePlan[] _children;
    private readonly byte[] _prefix;
    internal BmffBoxHeader Header { get; }
    internal long PayloadSize { get; }
    internal long Size => Header.HeaderSize + PayloadSize;

    private BmffWritePlan(BmffBox box, BmffBoxHeader header, long payloadSize, BmffWritePlan[] children, byte[] prefix)
    {
        _box = box;
        Header = header;
        PayloadSize = payloadSize;
        _children = children;
        _prefix = prefix;
    }

    internal static BmffWritePlan Create(BmffBox box, int depth, int maxDepth, bool topLevel) =>
        Create(box, depth, maxDepth, topLevel, new HashSet<BmffBox>(ReferenceEqualityComparer.Instance));

    private static BmffWritePlan Create(BmffBox box, int depth, int maxDepth, bool topLevel, HashSet<BmffBox> ancestors)
    {
        if (box is null || !ancestors.Add(box))
        {
            throw new ContentFormatException(ErrorMessages.InvalidTree, ContentFormatErrorCode.InvalidStructure);
        }
        if (depth > maxDepth)
        {
            throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, box.Offset);
        }
        try
        {
            long payloadSize;
            var children = Array.Empty<BmffWritePlan>();
            var prefix = Array.Empty<byte>();
            if (box is BmffBoxComposite composite)
            {
                using var memory = new MemoryStream();
                using (var stream = new BmffStream(memory, 0, composite.PrefixSize))
                {
                    composite.WritePrefix(stream);
                    if (stream.Position != composite.PrefixSize)
                    {
                        throw new ContentFormatException(ErrorMessages.IncompleteBox, ContentFormatErrorCode.InvalidStructure, box.Offset);
                    }
                }
                prefix = memory.ToArray();
                payloadSize = 0;
                children = new BmffWritePlan[composite.Children.Count];
                for (var index = 0; index < children.Length; index++)
                {
                    children[index] = Create(composite.Children[index], depth + 1, maxDepth, false, ancestors);
                    if (children[index].Size > long.MaxValue - payloadSize)
                    {
                        throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, box.Offset);
                    }
                    payloadSize += children[index].Size;
                }
                payloadSize = composite.MeasurePayload(payloadSize);
            }
            else
            {
                payloadSize = box.GetPayloadSize();
            }
            var minimumHeader = box.BoxType == BmffBoxType.uuid ? 24 : 8;
            if (payloadSize > uint.MaxValue - minimumHeader)
            {
                minimumHeader += 8;
            }
            if (payloadSize < 0 || payloadSize > long.MaxValue - minimumHeader)
            {
                throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, box.Offset);
            }
            var original = box.Header;
            if (original.ExtendsToEnd && !topLevel)
            {
                throw new ContentFormatException(ErrorMessages.NestedZeroSize, ContentFormatErrorCode.InvalidStructure, box.Offset);
            }
            // Unchanged headers retain even non-minimal large encodings for byte-for-byte preservation.
            var header = original.HeaderSize != 0 && original.TotalSize - original.HeaderSize == payloadSize
                ? original
                : BmffBoxHeader.Create(box.BoxType, payloadSize, extendedType: original.ExtendedType);
            return new BmffWritePlan(box, header, payloadSize, children, prefix);
        }
        finally
        {
            ancestors.Remove(box);
        }
    }

    internal void Write(BmffStream output)
    {
        Header.Write(output);
        using var payload = new BmffStream(output, output.Position, PayloadSize) { WritePlan = this };
        _box.Write(payload);
        if (payload.Position != PayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.IncompleteBox, ContentFormatErrorCode.InvalidStructure, payload.Offset + payload.Position);
        }
    }

    internal void WriteChildren(BmffStream output)
    {
        foreach (var child in _children)
        {
            child.Write(output);
        }
    }

    internal async ValueTask WriteAsync(BmffStream output, int bufferLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var header = new MemoryStream(32);
        Header.Write(header);
        await output.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken).ConfigureAwait(false);
        using var payload = new BmffStream(output, output.Position, PayloadSize) { WritePlan = this };
        payload.CancellationToken = cancellationToken;
        if (_box is BmffBoxComposite { SupportsAsyncPlan: true } composite)
        {
            await payload.WriteAsync(_prefix, cancellationToken).ConfigureAwait(false);
            foreach (var child in _children)
            {
                await child.WriteAsync(payload, bufferLimit, cancellationToken).ConfigureAwait(false);
            }
            await payload.WriteAsync(composite.TrailingData, cancellationToken).ConfigureAwait(false);
        }
        else if (_box is BmffBoxComposite)
        {
            // Third-party composites retain their synchronous per-box Write contract.
            _box.Write(payload);
        }
        else if (TryGetSegment(out var segment))
        {
            await segment.CopyToAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (PayloadSize > bufferLimit)
            {
                throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, _box.Offset);
            }
            using var memory = new BmffBufferStream(bufferLimit);
            using (var buffer = new BmffStream(memory, 0, PayloadSize))
            {
                _box.Write(buffer);
                if (buffer.Position != PayloadSize)
                {
                    throw new ContentFormatException(ErrorMessages.IncompleteBox, ContentFormatErrorCode.InvalidStructure, _box.Offset);
                }
            }
            await payload.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length), cancellationToken).ConfigureAwait(false);
        }
        if (payload.Position != PayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.IncompleteBox, ContentFormatErrorCode.InvalidStructure, payload.Offset + payload.Position);
        }
    }

    private bool TryGetSegment(out BinarySegment segment)
    {
        switch (_box)
        {
            case UnknownBox unknown: segment = unknown.Payload; return true;
            case MediaDataBox media: segment = media.Payload; return true;
            case FreeSpaceBox free: segment = free.Payload; return true;
            case SkipBox skip: segment = skip.Payload; return true;
            default: segment = default; return false;
        }
    }
}
