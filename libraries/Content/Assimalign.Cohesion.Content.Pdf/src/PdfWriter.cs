using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>Writes PDF documents to a separate, forward-only destination (§7.5).</summary>
/// <remarks>The destination is borrowed. Its current position becomes the output PDF origin and it
/// must have no trailing bytes. Source and destination must use distinct underlying storage.
/// WriteAsync currently performs synchronous I/O with cancellation between bounded writes.
/// Saving does not rebase or clear document edits; reload the result to start a new revision.</remarks>
[global::System.Diagnostics.DebuggerDisplay("PdfWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class PdfWriter : IContentWriter<PdfDocument>
{
    private readonly PdfWriterOptions _options;
    /// <summary>Creates a full-save writer with classic cross-references.</summary>
    public PdfWriter() : this(new PdfWriterOptions()) { }
    private PdfWriter(PdfWriterOptions options) => _options = options;
    /// <summary>Creates a writer with validated save choices and limits.</summary>
    /// <param name="options">The save configuration.</param>
    /// <returns>The configured writer.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is invalid.</exception>
    /// <exception cref="ArgumentException">Object packing is requested without xref streams.</exception>
    public static PdfWriter Create(PdfWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(); return new(options);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream or document is null.</exception>
    /// <exception cref="ArgumentException">The destination is not writable, aliases the source, or has trailing bytes.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">Encryption, invalid structure, an unsupported version or a limit prevents saving.</exception>
    public void Write(Stream stream, PdfDocument document) => Save(stream, document, default);
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream or document is null.</exception>
    /// <exception cref="ArgumentException">The destination is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The document cannot be saved under this configuration.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public ValueTask WriteAsync(Stream stream, PdfDocument document, CancellationToken cancellationToken = default)
    {
        Save(stream, document, cancellationToken); return ValueTask.CompletedTask;
    }
    private void Save(Stream stream, PdfDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        document.ValidateSaveDestination(stream);
        using var session = new PdfSaveSession(document, stream, _options, cancellationToken);
        if (_options.Incremental) { session.WriteIncremental(); }
        else { session.WriteFull(); }
    }

    private sealed class DebugView
    {
        public DebugView(PdfWriter value)
        {
        }
    }
}
