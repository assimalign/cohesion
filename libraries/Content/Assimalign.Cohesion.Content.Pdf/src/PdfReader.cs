using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>Loads PDF structure through IContentReader with random access (§7.5).</summary>
/// <remarks>Read begins at the current position, treated as the PDF origin. The source is borrowed by default.
/// ReadAsync performs bounded synchronous random-access parsing and observes cancellation between reads.</remarks>
public sealed class PdfReader : IContentReader<PdfDocument>
{
    private readonly PdfReaderOptions _options;
    private readonly bool _leaveOpen;
    /// <summary>Creates a strict reader that borrows its source.</summary>
    public PdfReader() : this(new PdfReaderOptions(), true) { }
    private PdfReader(PdfReaderOptions options, bool leaveOpen) { _options = options; _leaveOpen = leaveOpen; }
    /// <summary>Creates a reader with explicit limits and ownership.</summary>
    /// <param name="options">The validated limits and recovery policy.</param>
    /// <param name="leaveOpen">Whether disposing the document leaves the source open. Failed loads always leave it open.</param>
    /// <returns>The configured reader.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is invalid.</exception>
    public static PdfReader Create(PdfReaderOptions options, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return new(options, leaveOpen);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ContentFormatException">Input is malformed, exceeds a limit, or is not readable and seekable (Unsupported).</exception>
    public PdfDocument Read(Stream stream) => Load(stream, default);
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ContentFormatException">Input is malformed, exceeds a limit, or is not readable and seekable (Unsupported).</exception>
    public ValueTask<PdfDocument> ReadAsync(Stream stream, CancellationToken cancellationToken = default) => new(Load(stream, cancellationToken));
    private PdfDocument Load(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ContentFormatException(ErrorMessages.SourceNotSeekable, ContentFormatErrorCode.Unsupported);
        }
        if (stream.Position < 0 || stream.Position > stream.Length)
        {
            throw new ContentFormatException(ErrorMessages.HeaderInvalid, ContentFormatErrorCode.InvalidStructure, stream.Position);
        }
        return new PdfFileLoader(stream, stream.Position, _options, cancellationToken).Load(_leaveOpen);
    }
}
