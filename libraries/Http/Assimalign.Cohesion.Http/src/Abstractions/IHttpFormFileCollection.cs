using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

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
