using System;
using System.Buffers;
using System.Text;

using Assimalign.Cohesion.Database.Types.Text.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Types.Text;

/// <summary>A deterministic, immutable built-in text analyzer.</summary>
/// <remarks>Tokenizes Unicode 17 letters and decimal digits with marks continuing open tokens.
/// Categories are pinned in source. Case and optional accent folding use Types' pinned Unicode 17 tables.
/// English then removes stop words and applies the in-house Porter2 stemmer.</remarks>
public sealed class TextAnalyzer
{
    private TextAnalyzer(TextAnalyzerDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>Gets the default analyzer: simple version 1, preserving accents.</summary>
    public static TextAnalyzer Simple { get; } = new(TextAnalyzerDescriptor.Simple);

    /// <summary>Gets English version 1, preserving accents.</summary>
    public static TextAnalyzer English { get; } = new(TextAnalyzerDescriptor.English);

    /// <summary>Gets the complete identity callers must persist.</summary>
    public TextAnalyzerDescriptor Descriptor { get; }

    /// <summary>Resolves an exact persisted identity, refusing unavailable names or versions.</summary>
    /// <param name="descriptor">The persisted name, version and accent-folding option.</param>
    /// <returns>An analyzer with the requested identity.</returns>
    /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
    /// <exception cref="TextException">The name is unavailable or the version differs.</exception>
    public static TextAnalyzer Create(TextAnalyzerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Name is not ("simple" or "english"))
        {
            throw new TextException(TextErrorCode.UnknownAnalyzer, ErrorMessages.UnknownAnalyzer);
        }

        int version = descriptor.Name == "simple" ? TextAnalyzerDescriptor.Simple.Version : TextAnalyzerDescriptor.English.Version;
        if (descriptor.Version != version)
        {
            throw new TextException(TextErrorCode.AnalyzerVersionMismatch, ErrorMessages.AnalyzerVersionMismatch);
        }

        return new TextAnalyzer(descriptor);
    }

    /// <summary>Analyzes letter/digit runs into a sorted tsvector.</summary>
    /// <param name="text">The document, including empty text.</param>
    /// <param name="weight">The weight applied to retained positions; the default is D.</param>
    /// <returns>An immutable vector. Stop words leave position gaps. Words at least 2,047
    /// UTF-8 bytes are skipped without consuming a position, as in PostgreSQL 17's parser.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The weight is invalid.</exception>
    /// <exception cref="TextException">Unicode is invalid or the vector exceeds its size limit.</exception>
    public TextVector Analyze(string text, TextWeight weight = TextWeight.D)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((byte)weight, (byte)TextWeight.A);
        var builder = new TextVectorBuilder();
        int offset = 0, tokenStart = -1, tokenBytes = 0, position = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out Rune rune, out int consumed) != OperationStatus.Done)
            {
                throw new TextException(TextErrorCode.InvalidUnicode, ErrorMessages.InvalidUnicode);
            }

            if (TextUnicodeData.IsLetterOrDigit(rune.Value) || (tokenStart >= 0 && TextUnicodeData.IsContinuation(rune.Value)))
            {
                if (tokenStart < 0)
                {
                    tokenStart = offset;
                }

                // Saturate: an arbitrarily long token needs no arbitrarily large buffer.
                tokenBytes = Math.Min(TextVector.MaxLexemeBytes, tokenBytes + rune.Utf8SequenceLength);
            }
            else if (tokenStart >= 0)
            {
                AddToken(text.AsSpan(tokenStart, offset - tokenStart), tokenBytes, ref position, weight, builder);
                tokenStart = -1;
                tokenBytes = 0;
            }

            offset += consumed;
        }

        if (tokenStart >= 0)
        {
            AddToken(text.AsSpan(tokenStart), tokenBytes, ref position, weight, builder);
        }

        return builder.Build();
    }

    private void AddToken(ReadOnlySpan<char> token, int bytes, ref int position, TextWeight weight, TextVectorBuilder builder)
    {
        if (bytes >= TextVector.MaxLexemeBytes)
        {
            return;
        }

        position = Math.Min(position + 1, TextVector.MaxPosition);
        bool ascii = true, foldedAscii = true, asciiDigit = false;
        foreach (char c in token)
        {
            asciiDigit |= c is >= '0' and <= '9';
            ascii &= c <= 0x7f;
            foldedAscii &= c <= 0x7f && c is not (>= 'A' and <= 'Z');
        }

        // ASCII has no canonical accents. Already-folded ASCII is an identity in
        // Types' pinned tables; all other casing still goes through those tables.
        string lexeme = new(token);
        if (!foldedAscii)
        {
            Collation collation = Descriptor.FoldAccents && !ascii ? Collation.CaseAccentInsensitive : Collation.CaseInsensitive;
            lexeme = collation.Normalize(lexeme);
        }
        if (Descriptor.Name == "english" && !asciiDigit && bytes <= 1000)
        {
            if (EnglishStopWords.Contains(lexeme))
            {
                return;
            }

            lexeme = Porter2Stemmer.Stem(lexeme);
        }

        if (lexeme.Length > 0 && TextValidation.Utf8Length(lexeme) <= TextVector.MaxLexemeBytes)
        {
            builder.Add(lexeme, TextPosition.Create(position, weight));
        }
    }
}
