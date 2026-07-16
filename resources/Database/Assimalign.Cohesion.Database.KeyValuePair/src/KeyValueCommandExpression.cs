using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The syntax-tree node backing a key-value command statement. The key-value
/// model has no query language — commands are verbs with parameter operands — so
/// this expression only carries the operation and, when the command arrived as
/// text, the raw source.
/// </summary>
public sealed class KeyValueCommandExpression : QueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueCommandExpression"/>.
    /// </summary>
    /// <param name="operation">The command's operation.</param>
    /// <param name="text">The raw command text, when the command was parsed from the text seam.</param>
    public KeyValueCommandExpression(KeyValueOperation operation, string? text = null)
    {
        Operation = operation;
        Text = text;
    }

    /// <summary>
    /// Gets the command's operation.
    /// </summary>
    public KeyValueOperation Operation { get; }

    /// <inheritdoc />
    public override string? Text { get; }
}
