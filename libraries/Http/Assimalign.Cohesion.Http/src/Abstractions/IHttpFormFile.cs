using System.IO;

namespace Assimalign.Cohesion.Http;

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
