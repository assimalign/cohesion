namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label name in a label expression. A node matches when it carries the label; a relationship
/// matches when its type is the name. Names are ordinal and case-sensitive.
/// </summary>
/// <param name="Name">The label or relationship type name.</param>
public sealed record GqlLabelName(string Name) : GqlLabelExpression;
