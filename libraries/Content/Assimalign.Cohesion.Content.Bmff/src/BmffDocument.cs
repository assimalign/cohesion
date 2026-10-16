using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>An editable ordered box tree with borrowed or explicitly owned lazy source bytes.</summary>
/// <remarks>Keep the source open and unchanged until saving finishes. Access to one source is sequential.
/// This container layer does not relocate sample offsets; that belongs to the standard box models in #447.</remarks>
public sealed class BmffDocument : IDisposable
{
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
    public static ValueTask<BmffDocument> LoadAsync(Stream source, BmffReaderOptions? options = null, BmffBoxRegistry? registry = null, bool leaveOpen = true, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(LoadCore(source, options, registry, leaveOpen, cancellationToken));

    private static BmffDocument LoadCore(Stream source, BmffReaderOptions? options, BmffBoxRegistry? registry, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(ErrorMessages.DocumentSource, nameof(source));
        }
        var document = new BmffDocument { Source = source, _ownsSource = !leaveOpen };
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
    public void Save(Stream output) => SaveCore(output, 16 * 1024 * 1024, 64);

    internal void SaveCore(Stream output, int bufferLimit, int maxDepth)
    {
        ValidateOutput(output);
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
    public ValueTask SaveAsync(Stream output, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(output, 16 * 1024 * 1024, 64, cancellationToken);

    internal async ValueTask SaveCoreAsync(Stream output, int bufferLimit, int maxDepth, CancellationToken cancellationToken)
    {
        ValidateOutput(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var writer = BmffWriter.Create(output, bufferLimit, maxDepth);
        foreach (var box in Boxes)
        {
            await writer.WriteAsync(box, cancellationToken).ConfigureAwait(false);
        }
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
