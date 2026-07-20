using System;
using System.Buffers;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Tokenizes text into <see cref="TextToken"/> values by scanning for a configurable set of literal
/// tokens (<see cref="TextTokenizerOptions"/>), reporting the runs of text between matches as slices
/// of the input without copying. The tokenizer reads through <see cref="SequenceReader{T}"/>, so it
/// works over single- and multi-segment <see cref="ReadOnlySequence{T}"/> input alike — including
/// literals that span segment boundaries.
/// </summary>
/// <remarks>
/// <para>
/// With default options the only recognized tokens are the line terminators (<c>\r\n</c>, <c>\n</c>,
/// lone <c>\r</c>), so tokenization degenerates to the package line model: text runs separated by
/// <see cref="TextTokenKind.NewLine"/> tokens with terminator fidelity. Format parsers add their own
/// delimiters through <see cref="TextTokenizerOptions.Tokens"/>.
/// </para>
/// <para>
/// Matching is ordinal and leftmost; among definitions sharing a first character the longest literal
/// wins. Positions are physical: any <c>\n</c>, <c>\r\n</c>, or lone <c>\r</c> advances the reported
/// line whether it was matched as a token or flowed through a text run, so positions stay correct
/// even when the new-line defaults are removed.
/// </para>
/// <para>
/// The tokenizer is a stack-only <see langword="ref"/> struct; the tokens it produces are ordinary
/// structs that parsers may store. Constructing a tokenizer compiles the options' token table —
/// construct one per text to tokenize, not one per line.
/// </para>
/// </remarks>
public ref struct TextTokenizer
{
    private SequenceReader<char> _reader;
    private readonly TextTokenizerTable _table;
    private TextTokenKind _pendingKind;
    private TextTokenDefinition? _pendingDefinition;
    private ReadOnlySequence<char> _pendingValue;
    private bool _hasPending;
    private int _line;
    private int _column;
    private long _offset;
    private bool _carriageReturnPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextTokenizer"/> struct over a string.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="options">The tokens to recognize, or <see langword="null"/> for the default new-line tokens.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="options"/> contains a null definition or two definitions with the same text.</exception>
    public TextTokenizer(string text, TextTokenizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _reader = new SequenceReader<char>(new ReadOnlySequence<char>(text.AsMemory()));
        _table = options is null ? TextTokenizerTable.Default : TextTokenizerTable.Create(options);
        _line = 1;
        _column = 1;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TextTokenizer"/> struct over a memory of characters.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="options">The tokens to recognize, or <see langword="null"/> for the default new-line tokens.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="options"/> contains a null definition or two definitions with the same text.</exception>
    public TextTokenizer(ReadOnlyMemory<char> text, TextTokenizerOptions? options = null)
    {
        _reader = new SequenceReader<char>(new ReadOnlySequence<char>(text));
        _table = options is null ? TextTokenizerTable.Default : TextTokenizerTable.Create(options);
        _line = 1;
        _column = 1;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TextTokenizer"/> struct over a character sequence.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="options">The tokens to recognize, or <see langword="null"/> for the default new-line tokens.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="options"/> contains a null definition or two definitions with the same text.</exception>
    public TextTokenizer(ReadOnlySequence<char> text, TextTokenizerOptions? options = null)
    {
        _reader = new SequenceReader<char>(text);
        _table = options is null ? TextTokenizerTable.Default : TextTokenizerTable.Create(options);
        _line = 1;
        _column = 1;
    }

    /// <summary>Gets the position of the next token <see cref="TryRead"/> will produce, or the position after the final character once tokenization completes.</summary>
    public readonly TextPosition Position => new(_line, _column, _offset);

    /// <summary>
    /// Reads the next token.
    /// </summary>
    /// <param name="token">When this method returns <see langword="true"/>, the token that was read.</param>
    /// <returns><see langword="true"/> when a token was read; <see langword="false"/> at the end of the text.</returns>
    public bool TryRead(out TextToken token)
    {
        if (_hasPending)
        {
            _hasPending = false;
            token = Emit(_pendingKind, _pendingDefinition, _pendingValue);
            return true;
        }

        if (_reader.End)
        {
            token = default;
            return false;
        }

        var textStart = _reader.Position;
        while (true)
        {
            if (_table.Candidates.Length == 0 || !_reader.TryAdvanceToAny(_table.Candidates, advancePastDelimiter: false))
            {
                // No further candidate characters: the rest of the text is one text run.
                _reader.AdvanceToEnd();
                break;
            }

            var candidateStart = _reader.Position;
            if (_table.TryMatch(ref _reader, advancePast: true, out var definition))
            {
                var value = _reader.Sequence.Slice(candidateStart, _reader.Position);
                token = EmitOrHold(textStart, candidateStart, definition.Kind, definition, value);
                return true;
            }

            if (_table.TokenizeWhitespace && _reader.TryPeek(out var next) && next is ' ' or '\t')
            {
                var run = ReadWhitespaceRun();
                token = EmitOrHold(textStart, candidateStart, TextTokenKind.Whitespace, definition: null, run);
                return true;
            }

            // A candidate character that begins no token belongs to the surrounding text run.
            _reader.Advance(1);
        }

        token = Emit(TextTokenKind.Text, definition: null, _reader.Sequence.Slice(textStart, _reader.Position));
        return true;
    }

    /// <summary>
    /// Consumes a run of space and tab characters. When a definition starts with a space or tab the
    /// run is checked character by character so a literal starting mid-run still gets recognized on
    /// the next read; otherwise the run is consumed in one vectorized advance.
    /// </summary>
    private ReadOnlySequence<char> ReadWhitespaceRun()
    {
        var start = _reader.Position;
        if (_table.WhitespaceOverlapsDefinitions)
        {
            while (!_table.TryMatch(ref _reader, advancePast: false, out _)
                && _reader.TryPeek(out var next) && next is ' ' or '\t')
            {
                _reader.Advance(1);
            }
        }
        else
        {
            _reader.AdvancePastAny(' ', '\t');
        }

        return _reader.Sequence.Slice(start, _reader.Position);
    }

    /// <summary>
    /// Emits the matched token directly when no text precedes it; otherwise holds it and emits the
    /// preceding text run first, so callers always see text before the match that terminated it.
    /// </summary>
    private TextToken EmitOrHold(SequencePosition textStart, SequencePosition tokenStart, TextTokenKind kind, TextTokenDefinition? definition, in ReadOnlySequence<char> value)
    {
        var text = _reader.Sequence.Slice(textStart, tokenStart);
        if (text.IsEmpty)
        {
            return Emit(kind, definition, value);
        }

        _pendingKind = kind;
        _pendingDefinition = definition;
        _pendingValue = value;
        _hasPending = true;
        return Emit(TextTokenKind.Text, definition: null, text);
    }

    private TextToken Emit(TextTokenKind kind, TextTokenDefinition? definition, in ReadOnlySequence<char> value)
    {
        var token = new TextToken(kind, definition, value, new TextPosition(_line, _column, _offset));
        AdvancePosition(value);
        return token;
    }

    /// <summary>
    /// Advances the tracked position through an emitted value. Line breaks are counted physically —
    /// <c>\n</c>, <c>\r\n</c>, and lone <c>\r</c> each advance the line once, with a carriage return
    /// carried across value (and segment) boundaries so a split <c>\r\n</c> still counts once.
    /// </summary>
    private void AdvancePosition(in ReadOnlySequence<char> value)
    {
        foreach (var memory in value)
        {
            var span = memory.Span;
            var index = 0;
            while (index < span.Length)
            {
                if (_carriageReturnPending)
                {
                    _carriageReturnPending = false;
                    if (span[index] == '\n')
                    {
                        index++;
                        continue;
                    }
                }

                var breakIndex = span[index..].IndexOfAny('\r', '\n');
                if (breakIndex < 0)
                {
                    _column += span.Length - index;
                    break;
                }

                _line++;
                _column = 1;
                _carriageReturnPending = span[index + breakIndex] == '\r';
                index += breakIndex + 1;
            }
        }

        _offset += value.Length;
    }
}
