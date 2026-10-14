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
/// <para>
/// There are two lookups, and they never guess which one the caller meant.
/// <see cref="TryGetFromFileName(string, out string)"/> takes a file name and reads the type from
/// its final extension (so <c>archive.tar.gz</c> resolves as <c>.gz</c>).
/// <see cref="TryGetFromExtension(string, out string)"/> takes an extension, which must start with
/// a dot (<c>.css</c>). A file name with no extension maps to nothing: neither <c>html</c> nor the
/// dotfile <c>.json</c> is an HTML or JSON file, so a caller falls back to its own default instead
/// of serving either as an active type.
/// </para>
/// <para>
/// Lookups are case-insensitive. The table intentionally covers common web asset types rather
/// than the full IANA registry; a name or extension with no mapping falls back to
/// <see cref="Fallback"/> in the <c>Get</c> forms.
/// </para>
/// </remarks>
public static class HttpContentTypes
{
    /// <summary>The content type used when a name has no mapped extension: <c>application/octet-stream</c>.</summary>
    public const string Fallback = "application/octet-stream";

    // Extension (with leading dot, lower-case) → content type. Common web assets only.
    private static readonly KeyValuePair<string, string>[] _defaultMappings =
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
        = FrozenDictionary.ToFrozenDictionary(_defaultMappings, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to resolve a content type from a file name's extension using the default table.
    /// </summary>
    /// <remarks>
    /// The extension is the text from the last dot of the name's final segment (after the last
    /// <c>/</c> or <c>\</c>) to its end. Leading dots belong to the name, so a name with no dot
    /// (<c>html</c>, <c>README</c>), a dotfile (<c>.json</c>, <c>.env</c>), a name of dots
    /// (<c>.</c>, <c>..</c>), and a name that ends in a dot (<c>index.html.</c>) have no extension
    /// and map to nothing. A dotfile that has an extension of its own resolves by it
    /// (<c>.config.json</c> is JSON).
    /// </remarks>
    /// <param name="fileName">A file name, such as <c>site.css</c>. A path is accepted; only its final segment is read.</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type; otherwise <see cref="string.Empty"/>.</param>
    /// <returns><see langword="true"/> when the name has an extension and the extension is mapped; otherwise <see langword="false"/>.</returns>
    public static bool TryGetFromFileName(string fileName, out string contentType)
        => TryGetFromFileName(Default, fileName, out contentType);

    /// <summary>
    /// Attempts to resolve a content type from a file name's extension using a caller-supplied
    /// table (typically one built by <see cref="CreateMap(IEnumerable{KeyValuePair{string, string}})"/>).
    /// </summary>
    /// <remarks>
    /// The name is read as <see cref="TryGetFromFileName(string, out string)"/> reads it: a name
    /// with no extension maps to nothing, whatever keys <paramref name="mappings"/> holds.
    /// </remarks>
    /// <param name="mappings">The extension-to-content-type table to consult.</param>
    /// <param name="fileName">A file name, such as <c>site.css</c>. A path is accepted; only its final segment is read.</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type; otherwise <see cref="string.Empty"/>.</param>
    /// <returns><see langword="true"/> when the name has an extension and the extension is mapped; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mappings"/> is <see langword="null"/>.</exception>
    public static bool TryGetFromFileName(
        FrozenDictionary<string, string> mappings,
        string fileName,
        out string contentType)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        contentType = string.Empty;
        if (string.IsNullOrEmpty(fileName)
            || !TryGetExtension(fileName, out ReadOnlySpan<char> extension))
        {
            return false;
        }

        return TryLookup(mappings, extension.ToString(), out contentType);
    }

    /// <summary>
    /// Attempts to resolve a content type from an extension using the default table.
    /// </summary>
    /// <remarks>
    /// An extension is a dot followed by at least one character, none of them a dot or a path
    /// separator: exactly what <see cref="TryGetFromFileName(string, out string)"/> reads from a
    /// name. Anything else maps to nothing, so <c>css</c> is not read as <c>.css</c>, and a file
    /// name such as <c>site.css</c> is not an extension.
    /// </remarks>
    /// <param name="extension">An extension with its leading dot, such as <c>.css</c>.</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type; otherwise <see cref="string.Empty"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="extension"/> is an extension and is mapped; otherwise <see langword="false"/>.</returns>
    public static bool TryGetFromExtension(string extension, out string contentType)
        => TryGetFromExtension(Default, extension, out contentType);

    /// <summary>
    /// Attempts to resolve a content type from an extension using a caller-supplied table
    /// (typically one built by <see cref="CreateMap(IEnumerable{KeyValuePair{string, string}})"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="extension"/> must have the form <see cref="TryGetFromExtension(string, out string)"/>
    /// describes; anything else maps to nothing, whatever keys <paramref name="mappings"/> holds.
    /// </remarks>
    /// <param name="mappings">The extension-to-content-type table to consult.</param>
    /// <param name="extension">An extension with its leading dot, such as <c>.css</c>.</param>
    /// <param name="contentType">When this method returns <see langword="true"/>, the resolved content type; otherwise <see cref="string.Empty"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="extension"/> is an extension and is mapped; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mappings"/> is <see langword="null"/>.</exception>
    public static bool TryGetFromExtension(
        FrozenDictionary<string, string> mappings,
        string extension,
        out string contentType)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        contentType = string.Empty;
        if (!IsExtension(extension))
        {
            return false;
        }

        return TryLookup(mappings, extension, out contentType);
    }

    /// <summary>
    /// Resolves a content type from a file name's extension using the default table, returning
    /// <see cref="Fallback"/> when the name has no extension or the extension is not mapped.
    /// </summary>
    /// <param name="fileName">A file name, such as <c>site.css</c>. A path is accepted; only its final segment is read.</param>
    /// <returns>The resolved content type, or <see cref="Fallback"/>.</returns>
    public static string GetFromFileName(string fileName)
        => TryGetFromFileName(fileName, out string contentType) ? contentType : Fallback;

    /// <summary>
    /// Resolves a content type from an extension using the default table, returning
    /// <see cref="Fallback"/> when <paramref name="extension"/> is not an extension (it must start
    /// with a dot) or is not mapped.
    /// </summary>
    /// <param name="extension">An extension with its leading dot, such as <c>.css</c>.</param>
    /// <returns>The resolved content type, or <see cref="Fallback"/>.</returns>
    public static string GetFromExtension(string extension)
        => TryGetFromExtension(extension, out string contentType) ? contentType : Fallback;

    /// <summary>
    /// Builds a new content-type table from the defaults overlaid with
    /// <paramref name="additionalMappings"/>. Each override key may be given with or without a
    /// leading dot; a key that matches a default extension replaces the default value.
    /// </summary>
    /// <remarks>
    /// A key is always an extension, so the leading dot is optional here and nowhere else: the
    /// key <c>gltf</c> maps the extension <c>.gltf</c>, and a file named <c>gltf</c> still maps
    /// to nothing. A lookup reads only a name's final extension, so a key with an interior dot,
    /// such as <c>.tar.gz</c>, is never matched.
    /// </remarks>
    /// <param name="additionalMappings">The extension-to-content-type overrides to overlay, or <see langword="null"/>.</param>
    /// <returns>A frozen table combining the defaults and the overrides.</returns>
    public static FrozenDictionary<string, string> CreateMap(
        IEnumerable<KeyValuePair<string, string>>? additionalMappings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> mapping in _defaultMappings)
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

    private static bool TryLookup(FrozenDictionary<string, string> mappings, string extension, out string contentType)
    {
        if (mappings.TryGetValue(extension, out string? resolved))
        {
            contentType = resolved;
            return true;
        }

        contentType = string.Empty;
        return false;
    }

    private static bool TryGetExtension(string fileName, out ReadOnlySpan<char> extension)
    {
        // Only the final segment names the file: a dot in a directory ("assets.v2/html") is not
        // the file's extension.
        ReadOnlySpan<char> name = fileName.AsSpan();
        name = name[(name.LastIndexOfAny('/', '\\') + 1)..];

        // Leading dots belong to the name, so a dotfile (".json") is all name and no extension.
        // What remains has an extension only when it holds a dot that is not its last character:
        // "html" has no dot, and "index.html." ends in one.
        ReadOnlySpan<char> stem = name.TrimStart('.');
        int dot = stem.LastIndexOf('.');
        if (dot < 0 || dot == stem.Length - 1)
        {
            extension = default;
            return false;
        }

        extension = stem[dot..];
        return true;
    }

    private static bool IsExtension(string extension)
        => extension is { Length: > 1 }
            && extension[0] == '.'
            && extension.AsSpan(1).IndexOfAny('.', '/', '\\') < 0;

    private static string NormalizeKey(string key)
        => key[0] == '.' ? key : "." + key;
}
