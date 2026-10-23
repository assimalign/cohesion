using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Text.Internal;

/// <summary>
/// Counts bytes at the StreamReader/Decoder boundary. StreamReader passes its complete byte buffer
/// to GetChars, so end minus count is the buffer's absolute start, including after BOM removal.
/// DecoderFallbackException.Index can be negative for a sequence buffered by a preceding call.
/// Keeping the translation here covers every synchronous and asynchronous TextReader entry point.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("TextDecodingStream")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class TextDecodingStream : Stream
{
    private readonly Stream _source;
    private long _end;

    private TextDecodingStream(Stream source, long nonSeekableOrigin)
    {
        _source = source;
        _end = source.CanSeek ? source.Position : nonSeekableOrigin;
    }

    internal static TextReader CreateReader(Stream source, Encoding encoding, int skippedPreamble)
    {
        var tracked = new TextDecodingStream(source, skippedPreamble);
        return new StreamReader(tracked, new DecodingEncoding(encoding, tracked), detectEncodingFromByteOrderMarks: false);
    }

    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = _source.Read(buffer);
        _end += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _end += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    [global::System.Diagnostics.DebuggerDisplay("DecodingEncoding")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class DecodingEncoding : Encoding
    {
        private readonly Encoding _encoding;
        private readonly TextDecodingStream _stream;

        internal DecodingEncoding(Encoding encoding, TextDecodingStream stream) : base(encoding.CodePage)
        {
            _encoding = encoding;
            _stream = stream;
        }

        public override byte[] GetPreamble() => _encoding.GetPreamble();
        public override ReadOnlySpan<byte> Preamble => _encoding.Preamble;
        public override Decoder GetDecoder() => new ContentDecoder(_encoding.GetDecoder(), _stream);
        public override int GetMaxByteCount(int charCount) => _encoding.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => _encoding.GetMaxCharCount(byteCount);
        public override int GetByteCount(char[] chars, int index, int count) => _encoding.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => _encoding.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => _encoding.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => _encoding.GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        private sealed class DebugView
        {
            private readonly DecodingEncoding _target;

            public DebugView(DecodingEncoding value)
            {
                _target = value;
            }

            public string BodyName => _target.BodyName;
            public int CodePage => _target.CodePage;
            public global::System.Text.DecoderFallback DecoderFallback => _target.DecoderFallback;
            public global::System.Text.EncoderFallback EncoderFallback => _target.EncoderFallback;
            public string EncodingName => _target.EncodingName;
            public string HeaderName => _target.HeaderName;
            public bool IsBrowserDisplay => _target.IsBrowserDisplay;
            public bool IsBrowserSave => _target.IsBrowserSave;
            public bool IsMailNewsDisplay => _target.IsMailNewsDisplay;
            public bool IsMailNewsSave => _target.IsMailNewsSave;
            public bool IsReadOnly => _target.IsReadOnly;
            public bool IsSingleByte => _target.IsSingleByte;
            public string WebName => _target.WebName;
            public int WindowsCodePage => _target.WindowsCodePage;
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("ContentDecoder")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class ContentDecoder : Decoder
    {
        private readonly Decoder _decoder;
        private readonly TextDecodingStream _stream;

        internal ContentDecoder(Decoder decoder, TextDecodingStream stream)
        {
            _decoder = decoder;
            _stream = stream;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => _decoder.GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => GetChars(bytes, byteIndex, byteCount, chars, charIndex, flush: false);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            => GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex), flush);

        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
        {
            try
            {
                return _decoder.GetChars(bytes, chars, flush);
            }
            catch (DecoderFallbackException exception)
            {
                var position = _stream._end - bytes.Length + exception.Index;
                throw new ContentFormatException(ErrorMessages.DecodingInvalid, ContentFormatErrorCode.InvalidValue,
                    position >= 0 ? position : null, exception);
            }
        }

        private sealed class DebugView
        {
            private readonly ContentDecoder _target;

            public DebugView(ContentDecoder value)
            {
                _target = value;
            }

            public global::System.Text.DecoderFallback? Fallback => _target.Fallback;
            public global::System.Text.DecoderFallbackBuffer FallbackBuffer => _target.FallbackBuffer;
        }
    }

    private sealed class DebugView
    {
        private readonly TextDecodingStream _target;

        public DebugView(TextDecodingStream value)
        {
            _target = value;
        }

        public bool CanRead => _target.CanRead;
        public bool CanSeek => _target.CanSeek;
        public bool CanWrite => _target.CanWrite;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}
