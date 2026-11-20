using System;
using System.Diagnostics;
using System.Text.Json;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The definition of one keyword in a vocabulary: its name, where its value holds subschemas, and how
/// it evaluates.
/// </summary>
/// <remarks>
/// The built-in vocabularies define their keywords internally. A custom keyword supplies a factory
/// that the loader calls once for each occurrence of the keyword, with the keyword's value.
/// </remarks>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaKeywordDefinition
{
    private JsonSchemaKeywordDefinition(string name, KeywordKind kind, JsonSchemaSubschemaLayout subschemaLayout, Func<JsonElement, IJsonSchemaKeyword>? factory)
    {
        Name = name;
        Kind = kind;
        SubschemaLayout = subschemaLayout;
        Factory = factory;
    }

    /// <summary>
    /// Gets the keyword's name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets where the keyword's value holds subschemas.
    /// </summary>
    public JsonSchemaSubschemaLayout SubschemaLayout { get; }

    internal KeywordKind Kind { get; }

    internal Func<JsonElement, IJsonSchemaKeyword>? Factory { get; }

    /// <summary>
    /// Creates the definition of a custom keyword.
    /// </summary>
    /// <param name="name">The keyword's name.</param>
    /// <param name="factory">
    /// Creates the keyword's evaluator from its value, once per occurrence in a schema. It runs while the
    /// schema loads; an exception it throws fails the load, and so does a <see langword="null"/> result,
    /// with <see cref="InvalidOperationException"/>.
    /// </param>
    /// <param name="subschemaLayout">Where the keyword's value holds subschemas.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subschemaLayout"/> is not a defined value.</exception>
    public static JsonSchemaKeywordDefinition Create(string name, Func<JsonElement, IJsonSchemaKeyword> factory, JsonSchemaSubschemaLayout subschemaLayout = JsonSchemaSubschemaLayout.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (!Enum.IsDefined(subschemaLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(subschemaLayout));
        }

        return new JsonSchemaKeywordDefinition(name, KeywordKind.Custom, subschemaLayout, factory);
    }

    internal static JsonSchemaKeywordDefinition BuiltIn(string name, KeywordKind kind, JsonSchemaSubschemaLayout subschemaLayout = JsonSchemaSubschemaLayout.None) =>
        new(name, kind, subschemaLayout, null);

    private sealed class DebugView
    {
        private readonly JsonSchemaKeywordDefinition _definition;

        public DebugView(JsonSchemaKeywordDefinition definition)
        {
            _definition = definition;
        }

        public string Name => _definition.Name;

        public JsonSchemaSubschemaLayout SubschemaLayout => _definition.SubschemaLayout;
    }
}
