using System;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A loaded JSON Schema: a document root or one of its subschemas, ready to evaluate instances.
/// </summary>
/// <remarks>
/// Evaluation is thread-safe: one schema may evaluate any number of instances concurrently.
/// </remarks>
public interface IJsonSchema
{
    /// <summary>
    /// Gets the base URI of the schema resource that contains this schema, or <see langword="null"/>
    /// when the resource has no identifier.
    /// </summary>
    Uri? BaseUri { get; }

    /// <summary>
    /// Gets the location of this schema within its document.
    /// </summary>
    JsonPointer Location { get; }

    /// <summary>
    /// Gets the dialect this schema is processed with.
    /// </summary>
    JsonSchemaDialect Dialect { get; }

    /// <summary>
    /// Gets the schema's JSON value.
    /// </summary>
    JsonElement Source { get; }

    /// <summary>
    /// Gets the document that contains this schema.
    /// </summary>
    JsonSchemaDocument Document { get; }

    /// <summary>
    /// Evaluates an instance against this schema.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="options">The evaluation options, or <see langword="null"/> for <see cref="JsonSchemaEvaluationOptions.Default"/>.</param>
    /// <returns>The result, in the output format the options request.</returns>
    /// <exception cref="ArgumentException"><paramref name="instance"/> is <see langword="default"/>.</exception>
    /// <exception cref="JsonSchemaException">
    /// The schema cannot be processed: a reference cannot be resolved, a pattern timed out, or
    /// evaluation exceeded its depth limit. An invalid instance is a result, not an exception.
    /// </exception>
    JsonSchemaEvaluationResult Evaluate(JsonElement instance, JsonSchemaEvaluationOptions? options = null);

    /// <summary>
    /// Determines whether an instance is valid against this schema, stopping as soon as the outcome is
    /// known.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="options">The evaluation options, or <see langword="null"/> for <see cref="JsonSchemaEvaluationOptions.Default"/>. The output format is ignored.</param>
    /// <returns><see langword="true"/> when the instance is valid.</returns>
    /// <exception cref="ArgumentException"><paramref name="instance"/> is <see langword="default"/>.</exception>
    /// <exception cref="JsonSchemaException">The schema cannot be processed.</exception>
    bool IsValid(JsonElement instance, JsonSchemaEvaluationOptions? options = null);
}
