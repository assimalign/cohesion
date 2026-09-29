using System.Buffers;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// A token produced by <see cref="TextTokenizer"/>: its kind, the definition that matched (when one
/// did), its value as a slice of the tokenized text, and the position of its first character.
/// </summary>
/// <remarks>
/// The value is a slice of the input sequence — no characters are copied, and the token remains an
/// ordinary struct that parsers may store, unlike the stack-only tokenizer that produced it. The
/// slice is valid as long as the memory backing the tokenized sequence is.
/// </remarks>
public readonly struct TextToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextToken"/> structure.
    /// </summary>
    /// <param name="kind">The structural kind of the token.</param>
    /// <param name="definition">The definition that produced the token, or <see langword="null"/> for implicit text and whitespace runs.</param>
    /// <param name="value">The token value as a slice of the tokenized text.</param>
    /// <param name="position">The position of the token's first character.</param>
    public TextToken(TextTokenKind kind, TextTokenDefinition? definition, ReadOnlySequence<char> value, TextPosition position)
    {
        Kind = kind;
        Definition = definition;
        Value = value;
        Position = position;
    }

    /// <summary>Gets the structural kind of the token.</summary>
    public TextTokenKind Kind { get; }

    /// <summary>Gets the definition that produced the token, or <see langword="null"/> for implicit text and whitespace runs.</summary>
    public TextTokenDefinition? Definition { get; }

    /// <summary>Gets the caller-assigned identifier of the matched definition, or zero for implicit runs.</summary>
    public int Id => Definition?.Id ?? 0;

    /// <summary>Gets the token value as a slice of the tokenized text. No characters are copied.</summary>
    public ReadOnlySequence<char> Value { get; }

    /// <summary>Gets the position of the token's first character.</summary>
    public TextPosition Position { get; }

    /// <summary>Materializes the token value as a string.</summary>
    /// <returns>The token value.</returns>
    public override string ToString() => Value.ToString();
}
