using System;
using System.Buffers;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

// A bounded cursor independent of the shared source cursor. Nested resolutions can seek freely.
[global::System.Diagnostics.DebuggerDisplay("PdfInput")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfInput : IDisposable
{
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
    private readonly long? _errorAnchor;
    private long _bufferStart = -1;
    private int _bufferCount;
    private long _position;
    internal Stream Source { get; }
    internal long End { get; }
    internal CancellationToken CancellationToken { get; }
    internal long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > End)
            {
                throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, AbsolutePosition);
            }
            _position = value;
        }
    }
    internal long AbsolutePosition => _errorAnchor ?? Position + (Source is ContentSliceStream slice ? slice.SourceOffset : 0);
    internal PdfInput(Stream source, long start = 0, long? end = null, CancellationToken cancellationToken = default, long? errorAnchor = null)
    {
        Source = source; End = end ?? source.Length; Position = start;
        CancellationToken = cancellationToken; _errorAnchor = errorAnchor;
    }
    internal int Peek()
    {
        if (Position >= End) { return -1; }
        if (Position < _bufferStart || Position - _bufferStart >= _bufferCount)
        {
            CancellationToken.ThrowIfCancellationRequested();
            Source.Position = Position;
            _bufferStart = Position;
            _bufferCount = Source.Read(_buffer.AsSpan(0, (int)Math.Min(4096, End - Position)));
            if (_bufferCount == 0)
            {
                throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, AbsolutePosition);
            }
        }
        return _buffer[(int)(Position - _bufferStart)];
    }
    internal int Read() { var value = Peek(); if (value >= 0) { _position++; } return value; }
    internal int Required()
    {
        var value = Read();
        if (value < 0) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, AbsolutePosition); }
        return value;
    }
    internal void SkipWhiteSpace()
    {
        while (true)
        {
            while (IsWhiteSpace(Peek())) { Read(); }
            if (Peek() != '%') { return; }
            while (Peek() is not (-1 or 10 or 13)) { Read(); }
        }
    }
    internal static bool IsWhiteSpace(int value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    internal static bool IsDelimiter(int value) => value is -1 or '(' or ')' or '<' or '>' or '[' or ']' or '/' or '%' || IsWhiteSpace(value);
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);

    private sealed class DebugView
    {
        public DebugView(PdfInput value)
        {
        }
    }
}
