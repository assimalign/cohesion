using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Text.Internal;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Text content decoding an underlying <see cref="IContent"/> through a known encoding, skipping a
/// fixed-length byte order mark. The underlying content is owned or borrowed per the creation-time
/// <c>leaveOpen</c> flag; content-level metadata is forwarded unchanged.
/// </summary>
public sealed class TextContent : ITextContent
{
    private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Creates read-only, reopenable text content from a string, stored as UTF-8.
    /// </summary>
    /// <param name="text">The text value.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Read-only in-memory text content.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static TextContent Create(string text, ContentFormat? format = null, string? name = null, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = MemoryContent.Create(_utf8.GetBytes(text), format, name, mediaType);
        return new TextContent(content, _utf8, preambleLength: 0, leaveOpen: false);
    }

    /// <summary>
    /// Creates text content over existing content whose encoding is already known.
    /// </summary>
    /// <param name="content">The content carrying the encoded text.</param>
    /// <param name="encoding">The encoding used to decode the content's bytes.</param>
    /// <param name="leaveOpen"><see langword="false"/> (the default) to dispose <paramref name="content"/> with the text content; <see langword="true"/> to borrow it.</param>
    /// <returns>Text content decoding through <paramref name="encoding"/>.</returns>
    /// <exception cref="ArgumentNullException">The content or encoding is null.</exception>
    public static TextContent Create(IContent content, Encoding encoding, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);
        return new TextContent(content, encoding, preambleLength: 0, leaveOpen);
    }

    /// <summary>
    /// Creates text content over existing content, detecting the Unicode encoding from its leading
    /// bytes (byte order marks, then null-byte patterns, defaulting to UTF-8).
    /// </summary>
    /// <param name="content">The content carrying the encoded text. Must be reopenable, because detection consumes a read.</param>
    /// <param name="leaveOpen"><see langword="false"/> (the default) to dispose <paramref name="content"/> with the text content; <see langword="true"/> to borrow it.</param>
    /// <returns>Text content decoding through the detected encoding, skipping any byte order mark.</returns>
    /// <exception cref="ContentException">Thrown when <paramref name="content"/> is not reopenable.</exception>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    public static TextContent Create(IContent content, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanReopen)
        {
            throw new ContentException(ErrorMessages.DetectionRequiresReopen);
        }

        Span<byte> prefix = stackalloc byte[4];
        int read;
        using (var stream = content.OpenRead())
        {
            read = stream.ReadAtLeast(prefix, minimumBytes: 4, throwOnEndOfStream: false);
        }

        var detection = TextEncodingDetector.Detect(prefix[..read]);
        return new TextContent(content, detection.Encoding, detection.PreambleLength, leaveOpen);
    }

    private readonly IContent _content;
    private readonly int _preambleLength;
    private readonly bool _leaveOpen;
    private bool _disposed;

    private TextContent(IContent content, Encoding encoding, int preambleLength, bool leaveOpen)
    {
        _content = content;
        _preambleLength = preambleLength;
        _leaveOpen = leaveOpen;
        Encoding = encoding;
    }

    /// <inheritdoc/>
    public Encoding Encoding { get; }

    /// <inheritdoc/>
    public string? Name => _content.Name;

    /// <inheritdoc/>
    public ContentFormat Format => _content.Format;

    /// <inheritdoc/>
    public string? MediaType => _content.MediaType;

    /// <inheritdoc/>
    public long? Length => _content.Length;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool CanReopen => _content.CanReopen;

    /// <inheritdoc/>
    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _content.OpenRead();
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _content.OpenReadAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public TextReader OpenText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _content.OpenRead();
        SkipPreamble(stream);
        return TextDecodingStream.CreateReader(stream, Encoding, _preambleLength);
    }

    /// <inheritdoc/>
    public async ValueTask<TextReader> OpenTextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = await _content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        SkipPreamble(stream);
        return TextDecodingStream.CreateReader(stream, Encoding, _preambleLength);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _content.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            await _content.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SkipPreamble(Stream stream)
    {
        if (_preambleLength == 0)
        {
            return;
        }

        Span<byte> preamble = stackalloc byte[_preambleLength];
        stream.ReadAtLeast(preamble, _preambleLength, throwOnEndOfStream: false);
    }
}
