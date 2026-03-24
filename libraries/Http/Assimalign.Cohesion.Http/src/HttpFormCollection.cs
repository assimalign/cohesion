using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable parsed form collection.
/// </summary>
public sealed class HttpFormCollection : IHttpFormCollection
{
    private readonly Dictionary<string, HttpQueryValue> _values;

    /// <summary>
    /// Initializes an empty form collection.
    /// </summary>
    public HttpFormCollection()
    {
        _values = new Dictionary<string, HttpQueryValue>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public int Count => _values.Count;

    /// <inheritdoc />
    public HttpQueryValue this[string key] => _values.TryGetValue(key, out HttpQueryValue value) ? value : HttpQueryValue.Empty;

    public HttpFormFileCollection Files { get; } = new HttpFormFileCollection();

    /// <inheritdoc />
    IHttpFormFileCollection IHttpFormCollection.Files => Files;

    /// <summary>
    /// Adds a form value.
    /// </summary>
    /// <param name="key">The form key.</param>
    /// <param name="value">The form value.</param>
    public void Add(string key, HttpQueryValue value)
    {
        _values[key] = value;
    }

    /// <summary>
    /// Adds a form file.
    /// </summary>
    /// <param name="file">The file to add.</param>
    //public void Add(IHttpFormFile file)
    //{
    //    ((HttpFormFileCollection)Files).Add(file);
    //}

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        return _values.ContainsKey(key);
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, HttpQueryValue>> GetEnumerator()
    {
        return _values.GetEnumerator();
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, out HttpQueryValue value)
    {
        return _values.TryGetValue(key, out value);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}