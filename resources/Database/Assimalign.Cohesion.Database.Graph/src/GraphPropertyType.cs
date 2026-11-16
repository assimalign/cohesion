using System;

using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable scalar property declaration in a graph element type.</summary>
public sealed class GraphPropertyType
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal GraphPropertyTypeDeclaration Declaration { get; }

    private GraphPropertyType(GraphPropertyTypeDeclaration declaration)
        => (Declaration, Name, Type, Required) = (declaration, declaration.Name, declaration.Type, declaration.Required);

    /// <summary>Gets the ordinal, case-sensitive property name.</summary>
    public string Name { get; }
    /// <summary>Gets the scalar storage type, using the graph engine's numeric promotion rules.</summary>
    public DatabaseType Type { get; }
    /// <summary>Gets whether an absent or null value is refused (NOT NULL).</summary>
    public bool Required { get; }

    /// <summary>Creates a scalar declaration; nullable is the default.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="type">A supported graph scalar type.</param>
    /// <param name="required">Whether a non-null value is required.</param>
    /// <returns>The immutable declaration.</returns>
    /// <exception cref="ArgumentException">The name is blank or the scalar type is unsupported.</exception>
    public static GraphPropertyType Create(string name, DatabaseType type, bool required = false)
    {
        return new(GraphPropertyTypeDeclaration.Create(name, type, required));
    }

    internal static GraphPropertyType FromDeclaration(GraphPropertyTypeDeclaration declaration) => new(declaration);

    internal static bool Supports(DatabaseType type) => GraphPropertyTypeDeclaration.Supports(type);
}
