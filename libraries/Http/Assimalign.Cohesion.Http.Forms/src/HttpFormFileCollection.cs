using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable uploaded file collection.
/// </summary>
/// <remarks>
/// Every uploaded file part is kept, in the order the parts arrived. Several parts may share one field
/// name: RFC 7578 §4.3 sends the files of a multiple-file field (<c>&lt;input type="file" multiple&gt;</c>)
/// as separate parts with the same <c>name</c>. Enumerate the collection to read them all;
/// <see cref="TryGetValue(string, out HttpFormFile)"/> returns the first part with the name. Names
/// compare case-insensitively.
/// </remarks>
public sealed class HttpFormFileCollection : IHttpFormFileCollection
{
    private readonly List<HttpFormFile> _files = new();

    /// <inheritdoc />
    public int Count => _files.Count;

    /// <summary>
    /// Adds a file to the collection, after any file already added under the same name.
    /// </summary>
    /// <param name="file">The file to add.</param>
    public void Add(HttpFormFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files.Add(file);
    }

    /// <inheritdoc />
    public IEnumerator<IHttpFormFile> GetEnumerator()
    {
        return _files.GetEnumerator();
    }

    /// <summary>
    /// Attempts to retrieve the first file uploaded under a logical form name.
    /// </summary>
    /// <param name="name">The logical form name, compared case-insensitively.</param>
    /// <param name="file">The first file uploaded under the name, when found.</param>
    /// <returns><see langword="true"/> when a file was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(string name, out HttpFormFile file)
    {
        foreach (HttpFormFile candidate in _files)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                file = candidate;
                return true;
            }
        }

        file = null!;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    bool IHttpFormFileCollection.TryGetValue(string name, out IHttpFormFile file)
    {
        if (TryGetValue(name, out HttpFormFile found))
        {
            file = found;
            return true;
        }

        file = null!;
        return false;
    }
}
