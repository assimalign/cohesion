using System;
using System.Text;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Creates text content over common backing sources. The concrete implementations are internal; the
/// factory is the supported way to construct text content values.
/// </summary>
public static class TextContentFactory
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Creates read-only, reopenable text content from a string, stored as UTF-8.
    /// </summary>
    /// <param name="text">The text value.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Read-only in-memory text content.</returns>
    public static ITextContent FromString(string text, ContentFormat? format = null, string? name = null, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = ContentFactory.FromBytes(Utf8.GetBytes(text), format, name, mediaType);
        return new DecodedTextContent(content, Utf8, preambleLength: 0, leaveOpen: false);
    }

    /// <summary>
    /// Creates text content over existing content whose encoding is already known.
    /// </summary>
    /// <param name="content">The content carrying the encoded text.</param>
    /// <param name="encoding">The encoding used to decode the content's bytes.</param>
    /// <param name="leaveOpen"><see langword="false"/> (the default) to dispose <paramref name="content"/> with the text content; <see langword="true"/> to borrow it.</param>
    /// <returns>Text content decoding through <paramref name="encoding"/>.</returns>
    public static ITextContent FromContent(IContent content, Encoding encoding, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);
        return new DecodedTextContent(content, encoding, preambleLength: 0, leaveOpen);
    }

    /// <summary>
    /// Creates text content over existing content, detecting the Unicode encoding from its leading
    /// bytes (byte order marks, then null-byte patterns, defaulting to UTF-8).
    /// </summary>
    /// <param name="content">The content carrying the encoded text. Must be reopenable, because detection consumes a read.</param>
    /// <param name="leaveOpen"><see langword="false"/> (the default) to dispose <paramref name="content"/> with the text content; <see langword="true"/> to borrow it.</param>
    /// <returns>Text content decoding through the detected encoding, skipping any byte order mark.</returns>
    /// <exception cref="ContentException">Thrown when <paramref name="content"/> is not reopenable.</exception>
    public static ITextContent FromContent(IContent content, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanReopen)
        {
            throw new ContentException("Encoding detection requires reopenable content because it consumes a read. Provide the encoding explicitly for single-use content.");
        }

        Span<byte> prefix = stackalloc byte[4];
        int read;
        using (var stream = content.OpenRead())
        {
            read = stream.ReadAtLeast(prefix, minimumBytes: 4, throwOnEndOfStream: false);
        }

        var detection = TextEncodingDetector.Detect(prefix[..read]);
        return new DecodedTextContent(content, detection.Encoding, detection.PreambleLength, leaveOpen);
    }
}
