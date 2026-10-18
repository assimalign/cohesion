namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The inclusive repetition bounds of a relationship or parenthesized path.</summary>
/// <param name="Min">The minimum number of repetitions.</param>
/// <param name="Max">The maximum number of repetitions, or null for an unbounded quantifier.</param>
/// <remarks>The parser and planner validate bounds; an unbounded trail has a runtime limit of 64 relationships.</remarks>
public sealed record GqlQuantifier(int Min, int? Max);
