using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable parsed form collection.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpFormCollection {Count,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    /// <summary>Gets the uploaded files in arrival order.</summary>
    public HttpFormFileCollection Files { get; } = new HttpFormFileCollection();

    /// <inheritdoc />
    IHttpFormFileCollection IHttpFormCollection.Files => Files;

    /// <summary>
    /// Appends form values, preserving all existing occurrences in arrival order.
    /// </summary>
    /// <param name="key">The form key.</param>
    /// <param name="value">The form value.</param>
    public void Add(string key, HttpQueryValue value)
    {
        _values[key] = _values.TryGetValue(key, out HttpQueryValue existing)
            ? HttpQueryValue.Concat(existing, value)
            : value;
    }

    /// <summary>
    /// Adds a form file.
    /// </summary>
    /// <param name="file">The file to add.</param>
    public void Add(HttpFormFile file)
    {
        ((HttpFormFileCollection)Files).Add(file);
    }

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

    private sealed class DebugView
    {
        private readonly HttpFormCollection _target;

        public DebugView(HttpFormCollection value)
        {
            _target = value;
        }

        public int Count => _target.Count;
        public HttpFormFileCollection Files => _target.Files;
    }
}