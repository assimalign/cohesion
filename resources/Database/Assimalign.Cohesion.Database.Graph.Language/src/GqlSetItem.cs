using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ordered SET item targeting a bound node or relationship.</summary>
// Deviates from the repo interface-first rule per design decision: Database AST variants are concrete-first (owner, 2026-10-04; database-area.md).
public abstract record GqlSetItem
{
    /// <summary>Initializes an item in this assembly's variant family.</summary>
    /// <param name="variable">The target variable.</param><param name="location">The source span.</param>
    private protected GqlSetItem(string variable, Location? location = null) { Variable = variable; Location = location; }
    /// <summary>Gets the target variable.</summary>
    public string Variable { get; }
    /// <summary>Gets the source span.</summary>
    public Location? Location { get; }
}
