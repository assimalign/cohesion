using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A node in the executable GQL expression tree.</summary>
public abstract class GqlExpression : QueryExpression
{
    private string? _text;

    /// <summary>Initializes an expression with its source span.</summary>
    /// <param name="location">The optional span, using exclusive end offsets.</param>
    protected GqlExpression(Location? location = null) => Location = location;

    /// <inheritdoc />
    public override string? Text => _text;
    /// <inheritdoc />
    public override Location? Location { get; }

    internal void SetStatementText(string text) => _text = text;
}
