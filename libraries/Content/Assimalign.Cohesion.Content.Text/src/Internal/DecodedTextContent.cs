using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Text content decoding an underlying <see cref="IContent"/> through a known encoding, skipping a
/// fixed-length byte order mark. The underlying content is owned or borrowed per the creation-time
/// <c>leaveOpen</c> flag; content-level metadata is forwarded unchanged.
/// </summary>
internal sealed class DecodedTextContent : ITextContent
{
    private readonly IContent _content;
    private readonly int _preambleLength;
    private readonly bool _leaveOpen;
    private bool _disposed;

    internal DecodedTextContent(IContent content, Encoding encoding, int preambleLength, bool leaveOpen)
    {
        _content = content;
        _preambleLength = preambleLength;
        _leaveOpen = leaveOpen;
        Encoding = encoding;
    }

    public Encoding Encoding { get; }

    public string? Name => _content.Name;

    public ContentFormat Format => _content.Format;

    public string? MediaType => _content.MediaType;

    public long? Length => _content.Length;

    public bool IsReadOnly => true;

    public bool CanReopen => _content.CanReopen;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _content.OpenRead();
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _content.OpenReadAsync(cancellationToken);
    }

    public TextReader OpenText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _content.OpenRead();
        SkipPreamble(stream);
        return new StreamReader(stream, Encoding, detectEncodingFromByteOrderMarks: false);
    }

    public async ValueTask<TextReader> OpenTextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = await _content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        SkipPreamble(stream);
        return new StreamReader(stream, Encoding, detectEncodingFromByteOrderMarks: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _content.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            await _content.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SkipPreamble(Stream stream)
    {
        if (_preambleLength == 0)
        {
            return;
        }

        Span<byte> preamble = stackalloc byte[_preambleLength];
        stream.ReadAtLeast(preamble, _preambleLength, throwOnEndOfStream: false);
    }
}
