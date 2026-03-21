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
        Files = new HttpFormFileCollection();
    }

    /// <inheritdoc />
    public int Count => _values.Count;

    /// <inheritdoc />
    public HttpQueryValue this[string key] => _values.TryGetValue(key, out HttpQueryValue value) ? value : HttpQueryValue.Empty;

    /// <inheritdoc />
    public IHttpFormFileCollection Files { get; }

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
    public void Add(IHttpFormFile file)
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
}

/// <summary>
/// Provides a mutable uploaded file collection.
/// </summary>
public sealed class HttpFormFileCollection : IHttpFormFileCollection
{
    private readonly Dictionary<string, IHttpFormFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public int Count => _files.Count;

    /// <summary>
    /// Adds a file to the collection.
    /// </summary>
    /// <param name="file">The file to add.</param>
    public void Add(IHttpFormFile file)
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
    public bool TryGetValue(string name, out IHttpFormFile file)
    {
        return _files.TryGetValue(name, out file!);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

/// <summary>
/// Provides a concrete uploaded form file implementation.
/// </summary>
public sealed class HttpFormFile : IHttpFormFile
{
    private readonly Func<Stream> _streamFactory;

    /// <summary>
    /// Initializes a new uploaded file instance.
    /// </summary>
    /// <param name="name">The logical form name.</param>
    /// <param name="fileName">The original file name.</param>
    /// <param name="streamFactory">The factory used to create readable streams.</param>
    /// <param name="length">The file length.</param>
    /// <param name="contentType">The declared content type.</param>
    public HttpFormFile(string name, string fileName, Func<Stream> streamFactory, long length, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(streamFactory);

        Name = name;
        FileName = fileName;
        _streamFactory = streamFactory;
        Length = length;
        ContentType = contentType;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string FileName { get; }

    /// <inheritdoc />
    public string? ContentType { get; }

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
    public Stream OpenReadStream()
    {
        return _streamFactory();
    }
}
