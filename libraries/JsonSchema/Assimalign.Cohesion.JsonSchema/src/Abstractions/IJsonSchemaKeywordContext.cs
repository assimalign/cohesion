using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// What a custom keyword sees while it evaluates, and the operations it may perform.
/// </summary>
/// <remarks>
/// <para>
/// The context is valid only during <see cref="IJsonSchemaKeyword.Evaluate(IJsonSchemaKeywordContext)"/>;
/// afterwards <see cref="InstanceLocation"/> and every operation throw
/// <see cref="System.InvalidOperationException"/>. Subschemas can be applied only where the keyword's
/// <see cref="JsonSchemaSubschemaLayout"/> placed them; the loader compiled them with the rest of the
/// schema.
/// </para>
/// <para>
/// What a valid subschema applied to the current instance evaluated counts as evaluated by the keyword,
/// as for <c>allOf</c>. A subschema applied to a member or an item does not mark that member or item:
/// call <see cref="MarkPropertyEvaluated(string)"/> or <see cref="MarkItemEvaluated(int)"/> for each one
/// the keyword covers, as <c>properties</c> and <c>items</c> do.
/// </para>
/// </remarks>
public interface IJsonSchemaKeywordContext
{
    /// <summary>Gets the instance being evaluated.</summary>
    JsonElement Instance { get; }

    /// <summary>Gets the location of the instance within the root instance.</summary>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    JsonPointer InstanceLocation { get; }

    /// <summary>Gets the keyword's name.</summary>
    string Keyword { get; }

    /// <summary>Gets the keyword's value in the schema.</summary>
    JsonElement KeywordValue { get; }

    /// <summary>Gets the schema object that contains the keyword.</summary>
    IJsonSchema Schema { get; }

    /// <summary>Gets the dialect of the schema that contains the keyword.</summary>
    JsonSchemaDialect Dialect { get; }

    /// <summary>Gets the options the evaluation runs with.</summary>
    JsonSchemaEvaluationOptions Options { get; }

    /// <summary>
    /// Marks the keyword as failed.
    /// </summary>
    /// <param name="message">
    /// The error reported in the output, or <see langword="null"/> for a generic message. The message
    /// should not repeat instance values, which may be sensitive.
    /// </param>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    void Fail(string? message = null);

    /// <summary>
    /// Records the keyword's annotation, reported when the schema object is valid.
    /// </summary>
    /// <param name="annotation">The annotation value; it is copied.</param>
    /// <exception cref="System.ArgumentException"><paramref name="annotation"/> is <see langword="default"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    void Annotate(JsonElement annotation);

    /// <summary>
    /// Applies one of the keyword's subschemas to the current instance.
    /// </summary>
    /// <param name="subschema">
    /// The subschema's location relative to the keyword's value: <see cref="JsonPointer.Root"/> for
    /// <see cref="JsonSchemaSubschemaLayout.Schema"/>, an index for an array layout, or a member name
    /// for <see cref="JsonSchemaSubschemaLayout.SchemaMap"/>.
    /// </param>
    /// <returns><see langword="true"/> when the instance is valid against the subschema.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="subschema"/> is <see langword="default"/>, or the keyword has no subschema there.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    /// <exception cref="JsonSchemaException">The subschema cannot be processed.</exception>
    bool EvaluateSubschema(JsonPointer subschema);

    /// <summary>
    /// Applies one of the keyword's subschemas to a member of the current object instance.
    /// </summary>
    /// <param name="subschema">The subschema's location relative to the keyword's value.</param>
    /// <param name="propertyName">The name of the instance member to evaluate.</param>
    /// <returns><see langword="true"/> when the member is valid against the subschema.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="propertyName"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="subschema"/> is <see langword="default"/>, the keyword has no subschema there,
    /// or the instance has no member named <paramref name="propertyName"/>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    /// <exception cref="JsonSchemaException">The subschema cannot be processed.</exception>
    bool EvaluateSubschema(JsonPointer subschema, string propertyName);

    /// <summary>
    /// Applies one of the keyword's subschemas to an item of the current array instance.
    /// </summary>
    /// <param name="subschema">The subschema's location relative to the keyword's value.</param>
    /// <param name="index">The zero-based index of the instance item to evaluate.</param>
    /// <returns><see langword="true"/> when the item is valid against the subschema.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="subschema"/> is <see langword="default"/>, or the keyword has no subschema there.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">The instance has no item at <paramref name="index"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    /// <exception cref="JsonSchemaException">The subschema cannot be processed.</exception>
    bool EvaluateSubschema(JsonPointer subschema, int index);

    /// <summary>
    /// Records that the keyword evaluated a member of the current object instance, so
    /// <c>unevaluatedProperties</c> does not apply to it.
    /// </summary>
    /// <param name="propertyName">The member name.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="propertyName"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">The instance has no member named <paramref name="propertyName"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    void MarkPropertyEvaluated(string propertyName);

    /// <summary>
    /// Records that the keyword evaluated an item of the current array instance, so
    /// <c>unevaluatedItems</c> does not apply to it.
    /// </summary>
    /// <param name="index">The zero-based item index.</param>
    /// <exception cref="System.ArgumentOutOfRangeException">The instance has no item at <paramref name="index"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The keyword's evaluation has returned.</exception>
    void MarkItemEvaluated(int index);
}
