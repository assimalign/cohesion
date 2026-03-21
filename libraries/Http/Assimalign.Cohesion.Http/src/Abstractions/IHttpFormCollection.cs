using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents parsed form values and uploaded files.
/// </summary>
public interface IHttpFormCollection : IEnumerable<KeyValuePair<string, HttpQueryValue>>
{
    /// <summary>
    /// Gets the number of parsed form values.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the form value associated with the supplied key.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <returns>The parsed form value if present; otherwise <see cref="HttpQueryValue.Empty"/>.</returns>
    HttpQueryValue this[string key] { get; }

    /// <summary>
    /// Gets the uploaded files associated with the form.
    /// </summary>
    IHttpFormFileCollection Files { get; }

    /// <summary>
    /// Determines whether a form key exists.
    /// </summary>
    /// <param name="key">The key to test.</param>
    /// <returns><see langword="true"/> when the key exists; otherwise <see langword="false"/>.</returns>
    bool ContainsKey(string key);

    /// <summary>
    /// Attempts to retrieve a parsed form value.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <param name="value">The resolved value when found.</param>
    /// <returns><see langword="true"/> when the value was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(string key, out HttpQueryValue value);
}

/// <summary>
/// Represents the uploaded files associated with a parsed form.
/// </summary>
public interface IHttpFormFileCollection : IEnumerable<IHttpFormFile>
{
    /// <summary>
    /// Gets the number of uploaded files.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Attempts to retrieve a file by its logical form name.
    /// </summary>
    /// <param name="name">The logical form name.</param>
    /// <param name="file">The resolved file when found.</param>
    /// <returns><see langword="true"/> when a file was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(string name, out IHttpFormFile file);
}

/// <summary>
/// Represents an uploaded file within a parsed form.
/// </summary>
public interface IHttpFormFile
{
    /// <summary>
    /// Gets the logical form name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the original file name.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Gets the declared content type.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// Gets the content length.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Opens a readable stream for the file content.
    /// </summary>
    /// <returns>A readable file stream.</returns>
    Stream OpenReadStream();
}
