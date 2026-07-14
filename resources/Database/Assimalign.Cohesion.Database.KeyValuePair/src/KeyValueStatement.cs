using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A parsed key-value command statement: the operation a request executes. The
/// key-value model deliberately has no query language, so the statement is a
/// minimal member of the shared <see cref="QueryStatement"/> family — enough for
/// the execution contracts (<c>QueryRequest</c> carries a statement) without
/// inventing a syntax tree for verb commands.
/// </summary>
public sealed class KeyValueStatement : QueryStatement
{
    private readonly KeyValueCommandExpression _expression;

    /// <summary>
    /// Initializes a new <see cref="KeyValueStatement"/>.
    /// </summary>
    /// <param name="operation">The command's operation.</param>
    /// <param name="text">The raw command text, when the command was parsed from the text seam.</param>
    public KeyValueStatement(KeyValueOperation operation, string? text = null)
    {
        _expression = new KeyValueCommandExpression(operation, text);
    }

    /// <summary>
    /// Gets the command's operation.
    /// </summary>
    public KeyValueOperation Operation => _expression.Operation;

    /// <inheritdoc />
    public override QueryExpression Expression => _expression;
}
