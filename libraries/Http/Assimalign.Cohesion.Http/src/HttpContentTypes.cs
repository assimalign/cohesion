using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A static, AOT-safe mapping from file extension to content type for common web assets,
/// used by static-file serving and any consumer that must guess a representation's media type
/// from its name. The default table is a <see cref="FrozenDictionary{TKey, TValue}"/> built once
/// at startup with no reflection; consumers that need custom or additional mappings build their
/// own overlay table with <see cref="CreateMap(IEnumerable{KeyValuePair{string, string}})"/>.
/// </summary>
/// <remarks>
/// Lookups are case-insensitive and match on the final extension of a file name (so
/// <c>archive.tar.gz</c> resolves as <c>.gz</c>). The table intentionally covers common web
/// asset types rather than the full IANA registry; unknown extensions fall back to
/// <see cref="Fallback"/>.
/// </remarks>
public static class HttpContentTypes
{
    /// <summary>The content type used when an extension is unknown: <c>application/octet-stream</c>.</summary>
    public const string Fallback = "application/octet-stream";

    // Extension (with leading dot, lower-case) → content type. Common web assets only.
    private static readonly KeyValuePair<string, string>[] DefaultMappings =
    {
        // Documents / markup.
        new(".html", "text/html"),
        new(".htm", "text/html"),
        new(".xhtml", "application/xhtml+xml"),
        new(".css", "text/css"),
        new(".js", "text/javascript"),
        new(".mjs", "text/javascript"),
        new(".map", "application/json"),
        new(".json", "application/json"),
        new(".jsonld", "application/ld+json"),
        new(".webmanifest", "application/manifest+json"),
        new(".xml", "application/xml"),
        new(".rss", "application/rss+xml"),
        new(".atom", "application/atom+xml"),
        new(".txt", "text/plain"),
        new(".csv", "text/csv"),
        new(".md", "text/markdown"),
        new(".ics", "text/calendar"),
        new(".yaml", "application/yaml"),
        new(".yml", "application/yaml"),
        new(".wasm", "application/wasm"),
        new(".pdf", "application/pdf"),
        new(".rtf", "application/rtf"),

        // Images.
        new(".png", "image/png"),
        new(".apng", "image/apng"),
        new(".jpg", "image/jpeg"),
        new(".jpeg", "image/jpeg"),
        new(".gif", "image/gif"),
        new(".webp", "image/webp"),
        new(".avif", "image/avif"),
        new(".svg", "image/svg+xml"),
        new(".ico", "image/x-icon"),
        new(".bmp", "image/bmp"),
        new(".tif", "image/tiff"),
        new(".tiff", "image/tiff"),
        new(".heic", "image/heic"),
        new(".heif", "image/heif"),

        // Fonts.
        new(".woff", "font/woff"),
        new(".woff2", "font/woff2"),
        new(".ttf", "font/ttf"),
        new(".otf", "font/otf"),
        new(".eot", "application/vnd.ms-fontobject"),

        // Audio.
        new(".mp3", "audio/mpeg"),
        new(".m4a", "audio/mp4"),
        new(".aac", "audio/aac"),
        new(".oga", "audio/ogg"),
        new(".ogg", "audio/ogg"),
        new(".wav", "audio/wav"),
        new(".weba", "audio/webm"),
        new(".flac", "audio/flac"),

        // Video.
        new(".mp4", "video/mp4"),
        new(".m4v", "video/mp4"),
        new(".webm", "video/webm"),
        new(".ogv", "video/ogg"),
        new(".mov", "video/quicktime"),
        new(".avi", "video/x-msvideo"),
        new(".mpeg", "video/mpeg"),

        // Archives / binary.
        new(".zip", "application/zip"),
        new(".gz", "application/gzip"),
        new(".tar", "application/x-tar"),
        new(".7z", "application/x-7z-compressed"),
        new(".rar", "application/vnd.rar"),
        new(".bz2", "application/x-bzip2"),
        new(".bin", "application/octet-stream"),

        // Office documents.
        new(".doc", "application/msword"),
        new(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        new(".xls", "application/vnd.ms-excel"),
        new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        new(".ppt", "application/vnd.ms-powerpoint"),
        new(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
    };

    /// <summary>
    /// Gets the default extension-to-content-type table (case-insensitive keys, leading-dot form).
    /// </summary>
    public static FrozenDictionary<string, string> Default { get; }
        = FrozenDictionary.ToFrozenDictionary(DefaultMappings, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to resolve a content type from a file name or extension using the default table.
    /// </summary>
    /// <param name="fileNameOrExtension">A file name (e.g. <c>site.css</c>) or an extension (e.g. <c>.css</c> or <c>css</c>).</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type.</param>
    /// <returns><see langword="true"/> when the extension is mapped; otherwise <see langword="false"/>.</returns>
    public static bool TryGetContentType(string fileNameOrExtension, out string contentType)
        => TryGetContentType(Default, fileNameOrExtension, out contentType);

    /// <summary>
    /// Attempts to resolve a content type from a file name or extension using a caller-supplied table
    /// (typically one built by <see cref="CreateMap(IEnumerable{KeyValuePair{string, string}})"/>).
    /// </summary>
    /// <param name="mappings">The extension-to-content-type table to consult.</param>
    /// <param name="fileNameOrExtension">A file name or extension.</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type.</param>
    /// <returns><see langword="true"/> when the extension is mapped; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mappings"/> is <see langword="null"/>.</exception>
    public static bool TryGetContentType(
        FrozenDictionary<string, string> mappings,
        string fileNameOrExtension,
        out string contentType)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        contentType = string.Empty;
        if (string.IsNullOrEmpty(fileNameOrExtension)
            || !TryGetExtension(fileNameOrExtension, out string extension))
        {
            return false;
        }

        if (mappings.TryGetValue(extension, out string? resolved))
        {
            contentType = resolved;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves a content type from a file name or extension using the default table, returning
    /// <see cref="Fallback"/> when the extension is unknown.
    /// </summary>
    /// <param name="fileNameOrExtension">A file name or extension.</param>
    /// <returns>The resolved content type, or <see cref="Fallback"/>.</returns>
    public static string GetContentType(string fileNameOrExtension)
        => TryGetContentType(fileNameOrExtension, out string contentType) ? contentType : Fallback;

    /// <summary>
    /// Builds a new content-type table from the defaults overlaid with
    /// <paramref name="additionalMappings"/>. Each override key may be given with or without a
    /// leading dot; a key that matches a default extension replaces the default value.
    /// </summary>
    /// <param name="additionalMappings">The extension-to-content-type overrides to overlay, or <see langword="null"/>.</param>
    /// <returns>A frozen table combining the defaults and the overrides.</returns>
    public static FrozenDictionary<string, string> CreateMap(
        IEnumerable<KeyValuePair<string, string>>? additionalMappings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> mapping in DefaultMappings)
        {
            map[mapping.Key] = mapping.Value;
        }

        if (additionalMappings is not null)
        {
            foreach (KeyValuePair<string, string> mapping in additionalMappings)
            {
                if (string.IsNullOrEmpty(mapping.Key) || string.IsNullOrEmpty(mapping.Value))
                {
                    continue;
                }
                map[NormalizeKey(mapping.Key)] = mapping.Value;
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetExtension(string fileNameOrExtension, out string extension)
    {
        ReadOnlySpan<char> span = fileNameOrExtension.AsSpan();
        int dot = span.LastIndexOf('.');
        if (dot < 0)
        {
            // A bare extension token such as "css" — normalize to ".css".
            extension = "." + fileNameOrExtension;
            return true;
        }

        ReadOnlySpan<char> tail = span[dot..];
        if (tail.Length <= 1)
        {
            // A trailing dot with no extension characters.
            extension = string.Empty;
            return false;
        }

        extension = tail.ToString();
        return true;
    }

    private static string NormalizeKey(string key)
        => key[0] == '.' ? key : "." + key;
}
