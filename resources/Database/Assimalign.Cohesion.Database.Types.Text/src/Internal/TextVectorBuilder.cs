using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Types.Text.Internal;

internal sealed class TextVectorBuilder
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    private readonly Dictionary<string, TextLexemeBuilder> _lexemes = new(StringComparer.Ordinal);
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    private int _minimumBytes;

    internal void Add(string text, IReadOnlyList<TextPosition> positions)
    {
        TextLexemeBuilder builder = Get(text);
        foreach (TextPosition position in positions)
        {
            AddPosition(builder, position, TextVector.MaxPositionsPerLexeme);
        }
    }

    internal void Add(string text, TextPosition position)
        => AddPosition(Get(text), position, TextVector.MaxPositionsPerLexeme - 1);

    private TextLexemeBuilder Get(string text)
    {
        if (!_lexemes.TryGetValue(text, out TextLexemeBuilder? builder))
        {
            _minimumBytes += TextValidation.Utf8Length(text);
            CheckSize();
            builder = new TextLexemeBuilder();
            _lexemes.Add(text, builder);
        }

        return builder;
    }

    private void AddPosition(TextLexemeBuilder builder, TextPosition position, int limit)
    {
        int before = builder.Count;
        builder.Add(position, limit);
        if (builder.Count > before)
        {
            _minimumBytes += before == 0 ? 4 : 2;
            CheckSize();
        }
    }

    private void CheckSize()
    {
        if (_minimumBytes > TextVector.MaxPayloadBytes)
        {
            throw new TextException(TextErrorCode.VectorTooLarge, ErrorMessages.VectorTooLarge);
        }
    }

    internal TextVector Build()
    {
        if (_lexemes.Count == 0)
        {
            return TextVector.Empty;
        }

        string[] terms = new string[_lexemes.Count];
        _lexemes.Keys.CopyTo(terms, 0);
        Array.Sort(terms, TextValidation.Compare);
        var lexemes = new TextLexeme[terms.Length];
        int bytes = 0;
        for (int i = 0; i < terms.Length; i++)
        {
            string term = terms[i];
            TextPosition[] positions = _lexemes[term].Snapshot();
            bytes += TextValidation.Utf8Length(term);
            if (positions.Length > 0)
            {
                bytes = ((bytes + 1) & ~1) + 2 + 2 * positions.Length;
            }

            if (bytes > TextVector.MaxPayloadBytes)
            {
                throw new TextException(TextErrorCode.VectorTooLarge, ErrorMessages.VectorTooLarge);
            }

            lexemes[i] = new TextLexeme(term, positions);
        }

        return new TextVector(lexemes, bytes);
    }
}
