using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Describes the schema node that <see cref="JsonSchemaGenerationOptions.TransformSchemaNode"/> receives:
/// the contract it describes and where that contract is used.
/// </summary>
/// <remarks>
/// <para>
/// A type that the generator moves into <c>$defs</c> (<c>definitions</c> in draft-07) is described in two
/// kinds of node. Its definition is a node with no <see cref="PropertyInfo"/> and with
/// <see cref="IsReference"/> unset; each place it is used is a reference node, with
/// <see cref="IsReference"/> set and <see cref="PropertyInfo"/> naming the property when the value is
/// one. A type used in one place only is described inline: its definition and its use are the same node.
/// </para>
/// <para>
/// So a transform that adds what a type says about itself skips reference nodes, and one that adds
/// what a property says about its value acts where <see cref="PropertyInfo"/> is set.
/// </para>
/// </remarks>
public interface IJsonSchemaGenerationContext
{
    /// <summary>
    /// Gets the contract of the value the node describes. Where a <see cref="System.Nullable{T}"/> value
    /// is used, this is the nullable contract; the definition of its type has the underlying contract.
    /// </summary>
    JsonTypeInfo TypeInfo { get; }

    /// <summary>
    /// Gets the property whose value the node describes, or <see langword="null"/> for the root, a
    /// collection item, a dictionary value, a definition, or a derived type of a polymorphic type.
    /// </summary>
    JsonPropertyInfo? PropertyInfo { get; }

    /// <summary>
    /// Gets the polymorphic base type when the node describes one of its derived types, or
    /// <see langword="null"/> otherwise. Such a node is one alternative of the base type's
    /// <c>anyOf</c>.
    /// </summary>
    JsonTypeInfo? BaseTypeInfo { get; }

    /// <summary>
    /// Gets a value indicating whether the node refers to the definition of <see cref="TypeInfo"/>,
    /// with <c>$ref</c>, instead of describing the type inline. A nullable use refers to it from an
    /// <c>anyOf</c> that also allows <c>null</c>.
    /// </summary>
    bool IsReference { get; }
}
