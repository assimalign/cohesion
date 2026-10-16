using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>An editable ordered box tree with borrowed or explicitly owned lazy source bytes.</summary>
/// <remarks>Keep the source open and unchanged until saving finishes. Access to one source is sequential.
/// Save relocates supported in-file media references from the last loaded or planned layout.</remarks>
public sealed class BmffDocument : IDisposable
{
    private BmffLayout? _layout;
    private BmffBoxRegistry _registry = BmffBoxRegistry.Default;
    private bool _ownsSource;
    private bool _disposed;
    /// <summary>Gets the mutable ordered top-level boxes.</summary>
    public IList<BmffBox> Boxes { get; } = new List<BmffBox>();
    /// <summary>Gets the source of a loaded document, or null for a newly assembled document.</summary>
    public Stream? Source { get; private set; }

    /// <summary>Loads a tree while leaving opaque and media payloads lazy.</summary>
    /// <param name="source">A readable, seekable source at its first box.</param>
    /// <param name="options">Limits and unknown-box policy, or defaults.</param>
    /// <param name="registry">Factories, or defaults.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open; true by default.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentException">The source is not readable and seekable.</exception>
    /// <exception cref="ContentFormatException">The input is malformed or exceeds limits.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.ArgumentNullException">The source or destination is null.</exception>
    public static BmffDocument Load(Stream source, BmffReaderOptions? options = null, BmffBoxRegistry? registry = null, bool leaveOpen = true) =>
        LoadCore(source, options, registry, leaveOpen, default);

    /// <summary>Loads with cancellation checkpoints between headers and reads.</summary>
    /// <remarks>Parsing uses synchronous bounded reads; lazy payloads are not read.</remarks>
    /// <param name="source">A readable, seekable source.</param>
    /// <param name="options">Limits and policy.</param>
    /// <param name="registry">Factories.</param>
    /// <param name="leaveOpen">Whether the source is borrowed.</param>
    /// <param name="cancellationToken">Cancellation for the load.</param>
    /// <returns>A synchronously completed document task.</returns>
    /// <exception cref="ContentFormatException">The input is malformed or exceeds limits.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.ArgumentNullException">The source or destination is null.</exception>
    /// <exception cref="System.ArgumentException">The source is not readable and seekable.</exception>
    public static ValueTask<BmffDocument> LoadAsync(Stream source, BmffReaderOptions? options = null, BmffBoxRegistry? registry = null, bool leaveOpen = true, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(LoadCore(source, options, registry, leaveOpen, cancellationToken));

    private static BmffDocument LoadCore(Stream source, BmffReaderOptions? options, BmffBoxRegistry? registry, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(ErrorMessages.DocumentSource, nameof(source));
        }
        var document = new BmffDocument { Source = source, _ownsSource = !leaveOpen, _registry = registry ?? BmffBoxRegistry.Default };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = BmffReader.Create(source, options, registry);
            ((BmffReaderDefault)reader).SetCancellationToken(cancellationToken);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.Boxes.Add(reader.Current);
            }
            document._layout = BmffLayout.Original(document.Boxes);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Saves through a forward-only writer, copying lazy segments through.</summary>
    /// <param name="output">A separate borrowed output stream.</param>
    /// <exception cref="ContentFormatException">The tree is invalid or unsupported.</exception>
    /// <exception cref="InvalidOperationException">The output is the borrowed source.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.ArgumentNullException">The source or destination is null.</exception>
    /// <exception cref="System.ArgumentException">The destination is not writable.</exception>
    public void Save(Stream output) => SaveCore(output, 16 * 1024 * 1024, 64);

    internal void SaveCore(Stream output, int bufferLimit, int maxDepth)
    {
        ValidateOutput(output);
        RelocateCore(output.CanSeek ? output.Position : 0, maxDepth);
        using var writer = BmffWriter.Create(output, bufferLimit, maxDepth);
        foreach (var box in Boxes)
        {
            writer.Write(box);
        }
    }

    /// <summary>Saves with asynchronous output and lazy-segment copies.</summary>
    /// <param name="output">A separate borrowed output.</param>
    /// <param name="cancellationToken">Cancellation for writes and copies.</param>
    /// <returns>A task completing after the document is saved.</returns>
    /// <exception cref="ContentFormatException">The tree is invalid or unsupported.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.ArgumentNullException">The source or destination is null.</exception>
    /// <exception cref="System.ArgumentException">The destination is not writable.</exception>
    /// <exception cref="System.InvalidOperationException">The destination is the borrowed source.</exception>
    public ValueTask SaveAsync(Stream output, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(output, 16 * 1024 * 1024, 64, cancellationToken);

    internal async ValueTask SaveCoreAsync(Stream output, int bufferLimit, int maxDepth, CancellationToken cancellationToken)
    {
        ValidateOutput(output);
        cancellationToken.ThrowIfCancellationRequested();
        RelocateCore(output.CanSeek ? output.Position : 0, maxDepth);
        using var writer = BmffWriter.Create(output, bufferLimit, maxDepth);
        foreach (var box in Boxes)
        {
            await writer.WriteAsync(box, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Relocates supported media references to the current ordered tree and checkpoints its layout.</summary>
    /// <remarks>Also called by Save. Updates stco/co64, local file-based iloc, explicit tfhd bases,
    /// moof-relative trun data offsets, tfra moof offsets, resized mfro, sidx and saio, and registry rules. Promotes encodings as needed.
    /// Repeated calls are idempotent. New documents establish their first layout without shifting references.
    /// Unknown references and implicit bases after the first traf require a profile; see DESIGN.md.</remarks>
    /// <param name="outputOffset">The absolute start of the planned output, normally zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">The output offset is negative.</exception>
    /// <exception cref="ContentFormatException">The layout or a relocation is invalid or unsupported.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public void RelocateMediaOffsets(long outputOffset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(outputOffset);
        RelocateCore(outputOffset, 64);
    }

    /// <summary>Relocates references after a profile has compacted payload bytes.</summary>
    /// <param name="mapPayloadOffset">Maps a box's checkpoint payload-relative offset to its edited payload.
    /// The Boolean is true for an exclusive end boundary. Return a negative value for a deleted target;
    /// return the input offset for unaffected boxes. The callback must be pure across planning retries.</param>
    /// <param name="outputOffset">The absolute start of the planned output.</param>
    /// <remarks>The caller replaces payloads before calling and restores them if planning fails.
    /// Reference changes remain transactional. Local method-1 iloc extents also use the enclosing idat map.</remarks>
    /// <exception cref="ArgumentNullException">The mapper is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The output offset is negative.</exception>
    /// <exception cref="ContentFormatException">A reference cannot be mapped or represented.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public void RelocateMediaOffsets(Func<BmffBox, long, bool, long> mapPayloadOffset, long outputOffset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mapPayloadOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(outputOffset);
        _layout = _layout is null ? BmffLayout.Measure(Boxes, outputOffset, 64) :
            BmffOffsetRelocator.Relocate(Boxes, _layout, outputOffset, 64, _registry, mapPayloadOffset);
    }

    private void RelocateCore(long outputOffset, int maxDepth)
    {
        _layout = _layout is null
            ? BmffLayout.Measure(Boxes, outputOffset, maxDepth)
            : BmffOffsetRelocator.Relocate(Boxes, _layout, outputOffset, maxDepth, _registry);
    }

    private void ValidateOutput(Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(Source, output))
        {
            throw new InvalidOperationException(ErrorMessages.SameSourceOutput);
        }
        for (var index = 0; index < Boxes.Count - 1; index++)
        {
            if (Boxes[index].Header.ExtendsToEnd)
            {
                throw new ContentFormatException(ErrorMessages.NestedZeroSize, ContentFormatErrorCode.InvalidStructure, Boxes[index].Offset);
            }
        }
    }

    /// <summary>Disposes the source only when Load was given leaveOpen: false.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsSource)
            {
                Source?.Dispose();
            }
        }
    }
}
