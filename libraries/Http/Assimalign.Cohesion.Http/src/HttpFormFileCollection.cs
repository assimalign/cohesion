using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable uploaded file collection.
/// </summary>
public sealed class HttpFormFileCollection : IHttpFormFileCollection
{
    private readonly Dictionary<string, HttpFormFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public int Count => _files.Count;

    /// <summary>
    /// Adds a file to the collection.
    /// </summary>
    /// <param name="file">The file to add.</param>
    public void Add(HttpFormFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files[file.Name] = file;
    }

    /// <inheritdoc />
    public IEnumerator<IHttpFormFile> GetEnumerator()
    {
        return _files.Values.GetEnumerator();
    }

    /// <inheritdoc />
    public bool TryGetValue(string name, out HttpFormFile file)
    {
        return _files.TryGetValue(name, out file!);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    bool IHttpFormFileCollection.TryGetValue(string name, out IHttpFormFile? file)
    {
        file = default;
        if (TryGetValue(name, out HttpFormFile file1))
        {
            file = file1;
            return true;
        }
        return false;
    }
}
