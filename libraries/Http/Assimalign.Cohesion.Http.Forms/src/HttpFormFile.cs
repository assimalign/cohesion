using System;
using System.IO;

namespace Assimalign.Cohesion.Http;


/// <summary>
/// Provides a concrete uploaded form file implementation.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpFormFile {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly HttpFormFile _target;

        public DebugView(HttpFormFile value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public string FileName => _target.FileName;
        public string? ContentType => _target.ContentType;
        public long Length => _target.Length;
    }
}
