using System;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Reads text line by line, tracking one-based line numbers and reporting each line's terminator.
/// Recognizes <c>\n</c>, <c>\r\n</c>, and lone <c>\r</c>; performs no newline normalization and no
/// Unicode normalization — lines are reported exactly as authored.
/// </summary>
/// <remarks>
/// Line-derived formats (Markdown, plain text pipelines) build their segment models on this reader.
/// The reader owns the underlying <see cref="TextReader"/> and disposes it.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("TextLineReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class TextLineReader : IDisposable
{
    private readonly TextReader _reader;
    private readonly int _maxLineLength;
    private readonly StringBuilder _buffer = new();
    private long _position;
    private bool _lineTooLong;
    private int _lineNumber;
    private bool _disposed;
    private bool _completed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextLineReader"/> class over a text reader.
    /// </summary>
    /// <param name="reader">The reader to consume. The line reader takes ownership and disposes it.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is <see langword="null"/>.</exception>
    public TextLineReader(TextReader reader)
        : this(reader, null)
    {
    }

    /// <summary>Initializes a line reader with a bounded line buffer.</summary>
    /// <param name="reader">The reader to consume and dispose.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <exception cref="ArgumentNullException">The reader is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The maximum line length is not positive.</exception>
    public TextLineReader(TextReader reader, TextLineReaderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= TextLineReaderOptions.Default;
        options.Validate();
        _maxLineLength = options.MaxLineLength;
        _reader = reader;
    }

    /// <summary>Gets the one-based number of the most recently read line, or zero before the first read.</summary>
    public int LineNumber => _lineNumber;

    /// <summary>
    /// Reads the next line.
    /// </summary>
    /// <param name="line">When this method returns <see langword="true"/>, the line that was read.</param>
    /// <returns><see langword="true"/> when a line was read; <see langword="false"/> at the end of the text.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the reader has been disposed.</exception>
    /// <exception cref="ContentFormatException">A line exceeds MaxLineLength: LimitExceeded, with the first excess UTF-16 character's absolute offset. Subsequent reads repeat the failure.</exception>
    /// <exception cref="System.IO.IOException">The underlying text reader fails to read or peek.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">A caller-supplied text reader rejects malformed encoded text.</exception>
    public bool TryReadLine(out TextLine line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lineTooLong)
        {
            throw new ContentFormatException(ErrorMessages.LineLengthLimitExceeded, ContentFormatErrorCode.LimitExceeded, _position - 1);
        }

        if (_completed)
        {
            line = default;
            return false;
        }

        _buffer.Clear();
        while (true)
        {
            var next = _reader.Read();
            if (next < 0)
            {
                _completed = true;

                // End of text with nothing buffered: empty input and trailing terminators produce no
                // phantom final line.
                if (_buffer.Length == 0)
                {
                    line = default;
                    return false;
                }

                line = new TextLine(_buffer.ToString(), ++_lineNumber, TextLineEnding.None);
                return true;
            }

            _position++;
            var character = (char)next;
            if (character == '\n')
            {
                line = new TextLine(_buffer.ToString(), ++_lineNumber, TextLineEnding.LineFeed);
                return true;
            }

            if (character == '\r')
            {
                if (_reader.Peek() == '\n')
                {
                    _reader.Read();
                    _position++;
                    line = new TextLine(_buffer.ToString(), ++_lineNumber, TextLineEnding.CarriageReturnLineFeed);
                    return true;
                }

                line = new TextLine(_buffer.ToString(), ++_lineNumber, TextLineEnding.CarriageReturn);
                return true;
            }

            if (_buffer.Length >= _maxLineLength)
            {
                _lineTooLong = true;
                throw new ContentFormatException(ErrorMessages.LineLengthLimitExceeded, ContentFormatErrorCode.LimitExceeded, _position - 1);
            }
            _buffer.Append(character);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
    }

    private sealed class DebugView
    {
        private readonly TextLineReader _target;

        public DebugView(TextLineReader value)
        {
            _target = value;
        }

        public int LineNumber => _target.LineNumber;
    }
}
