namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A standalone schema mutation; names use ordinal, case-sensitive comparison.</summary>
public sealed record GqlSchemaCommand
{
    /// <summary>Gets whether this command drops its target.</summary>
    public bool Drop { get; init; }
    /// <summary>Gets the schema object kind.</summary>
    public GqlSchemaObjectKind Kind { get; init; }
    /// <summary>Gets the explicit name, or null to generate a rule name deterministically.</summary>
    public string? Name { get; init; }
    /// <summary>Gets whether IF EXISTS or IF NOT EXISTS suppresses an absent drop or existing create.</summary>
    public bool IfExists { get; init; }
    /// <summary>Gets the owning label or relationship type for a rule.</summary>
    public string? Definition { get; init; }
    /// <summary>Gets whether a constraint belongs to a relationship type.</summary>
    public bool Relationship { get; init; }
    /// <summary>Gets the schema-pattern variable.</summary>
    public string? Variable { get; init; }
    /// <summary>Gets the single property key.</summary>
    public string? Property { get; init; }
    /// <summary>Gets the type target, or null for an existence constraint.</summary>
    public GqlSchemaPropertyType? Type { get; init; }

    /// <summary>Renders normalized, reparsable schema DDL.</summary>
    /// <returns>The statement with quoted schema identifiers.</returns>
    public override string ToString()
    {
        string kind = Kind switch { GqlSchemaObjectKind.RelationshipType => "RELATIONSHIP TYPE", _ => Kind.ToString().ToUpperInvariant() };
        string prefix = (Drop ? "DROP " : "CREATE ") + kind + (Name is null ? "" : " " + Quote(Name)) +
            (IfExists ? Drop ? " IF EXISTS" : " IF NOT EXISTS" : "");
        if (Drop || Kind is GqlSchemaObjectKind.Label or GqlSchemaObjectKind.RelationshipType) { return prefix; }
        string variable = Quote(Variable ?? "n");
        string pattern = variable + ":" + Quote(Definition ?? "");
        string property = variable + "." + Quote(Property ?? "");
        return prefix + " FOR " + (Relationship ? "()-[" + pattern + "]-()" : "(" + pattern + ")") +
            (Kind == GqlSchemaObjectKind.Index ? " ON (" + property + ")" : " REQUIRE " + property +
                (Type is null ? " IS NOT NULL" : " IS :: " + Type.Value.ToString().ToUpperInvariant()));
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", System.StringComparison.Ordinal) + "\"";
}
