using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The result of an evaluation: one output unit of JSON Schema 2020-12 Core §12.4, with its nested
/// errors and annotations in the requested output format.
/// </summary>
/// <remarks>
/// <see cref="WriteTo(Utf8JsonWriter)"/> and <see cref="ToString"/> produce the specification's JSON
/// output. Error messages describe the failed constraint without repeating instance values.
/// </remarks>
[DebuggerDisplay("Valid = {IsValid}, {KeywordLocation} @ {InstanceLocation}")]
public sealed class JsonSchemaEvaluationResult
{
    private static readonly JsonSchemaEvaluationResult[] _none = [];
    private static readonly JsonSchemaEvaluationResult _flagValid = new(true, JsonSchemaOutputFormat.Flag);
    private static readonly JsonSchemaEvaluationResult _flagInvalid = new(false, JsonSchemaOutputFormat.Flag);

    private JsonSchemaEvaluationResult(bool isValid, JsonSchemaOutputFormat outputFormat)
    {
        IsValid = isValid;
        OutputFormat = outputFormat;
        KeywordLocation = JsonPointer.Root;
        InstanceLocation = JsonPointer.Root;
        Errors = _none;
        Annotations = _none;
    }

    private JsonSchemaEvaluationResult(OutputUnit unit, JsonSchemaOutputFormat outputFormat, bool keepAnnotation, IReadOnlyList<JsonSchemaEvaluationResult> errors, IReadOnlyList<JsonSchemaEvaluationResult> annotations)
    {
        IsValid = unit.Valid;
        OutputFormat = outputFormat;
        KeywordLocation = JsonPointer.CreateUnchecked(unit.KeywordLocation);
        InstanceLocation = JsonPointer.CreateUnchecked(unit.InstanceLocation);
        AbsoluteKeywordLocation = unit.Node.GetAbsoluteLocation(unit.Keyword);
        Keyword = unit.Keyword?.Name;
        Schema = unit.Node;
        Error = unit.Valid ? null : unit.Error;
        Annotation = keepAnnotation && unit.Valid ? unit.Annotation : null;
        Errors = errors;
        Annotations = annotations;
    }

    /// <summary>
    /// Gets a value indicating whether the instance is valid at this unit.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Gets the output format this result was produced in.
    /// </summary>
    public JsonSchemaOutputFormat OutputFormat { get; }

    /// <summary>
    /// Gets the evaluation path of the keyword or schema this unit reports: the JSON Pointer from the
    /// evaluated schema's root, through every reference followed.
    /// </summary>
    public JsonPointer KeywordLocation { get; }

    /// <summary>
    /// Gets the canonical URI of the keyword or schema this unit reports, or <see langword="null"/>
    /// when its schema resource has no identifier.
    /// </summary>
    public string? AbsoluteKeywordLocation { get; }

    /// <summary>
    /// Gets the location of the instance value this unit reports.
    /// </summary>
    public JsonPointer InstanceLocation { get; }

    /// <summary>
    /// Gets the name of the keyword this unit reports, or <see langword="null"/> for a unit that
    /// reports a whole schema.
    /// </summary>
    public string? Keyword { get; }

    /// <summary>
    /// Gets the schema this unit reports, or the schema that holds its keyword; <see langword="null"/>
    /// for a <see cref="JsonSchemaOutputFormat.Flag"/> result.
    /// </summary>
    public IJsonSchema? Schema { get; }

    /// <summary>
    /// Gets the error of an invalid unit, or <see langword="null"/>.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// Gets the annotation of a valid keyword unit, or <see langword="null"/>.
    /// </summary>
    public JsonElement? Annotation { get; }

    /// <summary>
    /// Gets the nested units that are invalid.
    /// </summary>
    public IReadOnlyList<JsonSchemaEvaluationResult> Errors { get; }

    /// <summary>
    /// Gets the nested units that are valid.
    /// </summary>
    public IReadOnlyList<JsonSchemaEvaluationResult> Annotations { get; }

    /// <summary>
    /// Writes the result as the specification's JSON output.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteBoolean("valid", IsValid);
        if (OutputFormat != JsonSchemaOutputFormat.Flag)
        {
            writer.WriteString("keywordLocation", KeywordLocation.Value);
            if (AbsoluteKeywordLocation is not null)
            {
                writer.WriteString("absoluteKeywordLocation", AbsoluteKeywordLocation);
            }

            writer.WriteString("instanceLocation", InstanceLocation.Value);
            if (Error is not null)
            {
                writer.WriteString("error", Error);
            }

            if (Annotation is JsonElement annotation)
            {
                writer.WritePropertyName("annotation");
                annotation.WriteTo(writer);
            }

            WriteUnits(writer, "errors", Errors);
            WriteUnits(writer, "annotations", Annotations);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns the result as the specification's JSON output.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public override string ToString()
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static JsonSchemaEvaluationResult Flag(bool isValid) => isValid ? _flagValid : _flagInvalid;

    internal static JsonSchemaEvaluationResult FromUnits(OutputUnit root, JsonSchemaOutputFormat format)
    {
        return format switch
        {
            JsonSchemaOutputFormat.Basic => BuildBasic(root),
            JsonSchemaOutputFormat.Detailed => BuildDetailed(root, isRoot: true, insideInvalid: false)!,
            _ => BuildVerbose(root, insideInvalid: false),
        };
    }

    private static void WriteUnits(Utf8JsonWriter writer, string name, IReadOnlyList<JsonSchemaEvaluationResult> units)
    {
        if (units.Count == 0)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (JsonSchemaEvaluationResult unit in units)
        {
            unit.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    // Verbose: the whole tree. Annotations inside an invalid schema are dropped (2020-12 Core §7.7.1.2),
    // and so are those of a schema applied to a derived value, such as a member name.
    private static JsonSchemaEvaluationResult BuildVerbose(OutputUnit unit, bool insideInvalid)
    {
        bool dropAnnotations = insideInvalid || unit.DiscardsAnnotations || (unit.Keyword is null && !unit.Valid);
        List<JsonSchemaEvaluationResult>? errors = null;
        List<JsonSchemaEvaluationResult>? annotations = null;
        if (unit.Children is not null)
        {
            foreach (OutputUnit child in unit.Children)
            {
                JsonSchemaEvaluationResult converted = BuildVerbose(child, dropAnnotations);
                if (child.Valid)
                {
                    (annotations ??= new()).Add(converted);
                }
                else
                {
                    (errors ??= new()).Add(converted);
                }
            }
        }

        return new JsonSchemaEvaluationResult(unit, JsonSchemaOutputFormat.Verbose, !dropAnnotations, (IReadOnlyList<JsonSchemaEvaluationResult>?)errors ?? _none, (IReadOnlyList<JsonSchemaEvaluationResult>?)annotations ?? _none);
    }

    // Detailed: keep only units on a path to an error (invalid result) or an annotation (valid
    // result), and replace a unit with no content of its own and one child by that child.
    private static JsonSchemaEvaluationResult? BuildDetailed(OutputUnit unit, bool isRoot, bool insideInvalid)
    {
        bool dropAnnotations = insideInvalid || unit.DiscardsAnnotations || (unit.Keyword is null && !unit.Valid);
        List<JsonSchemaEvaluationResult>? errors = null;
        List<JsonSchemaEvaluationResult>? annotations = null;
        if (unit.Children is not null)
        {
            foreach (OutputUnit child in unit.Children)
            {
                if (!unit.Valid && child.Valid)
                {
                    // An invalid unit reports why it failed, not what passed.
                    continue;
                }

                if (unit.Valid && !child.Valid)
                {
                    // A valid unit's failing children (an anyOf branch, a contains miss) are not errors.
                    continue;
                }

                JsonSchemaEvaluationResult? converted = BuildDetailed(child, isRoot: false, dropAnnotations);
                if (converted is null)
                {
                    continue;
                }

                if (child.Valid)
                {
                    (annotations ??= new()).Add(converted);
                }
                else
                {
                    (errors ??= new()).Add(converted);
                }
            }
        }

        bool hasOwnContent = unit.Valid ? (!dropAnnotations && unit.Annotation is not null) : unit.Error is not null;
        int childCount = (errors?.Count ?? 0) + (annotations?.Count ?? 0);
        if (!isRoot)
        {
            if (!hasOwnContent && childCount == 0)
            {
                return null;
            }

            if (!hasOwnContent && childCount == 1)
            {
                return errors is { Count: 1 } ? errors[0] : annotations![0];
            }
        }

        return new JsonSchemaEvaluationResult(unit, JsonSchemaOutputFormat.Detailed, !dropAnnotations, (IReadOnlyList<JsonSchemaEvaluationResult>?)errors ?? _none, (IReadOnlyList<JsonSchemaEvaluationResult>?)annotations ?? _none);
    }

    // Basic: a flat list under the root, of the errors along invalid paths, or of the annotations of a
    // valid evaluation.
    private static JsonSchemaEvaluationResult BuildBasic(OutputUnit root)
    {
        List<JsonSchemaEvaluationResult> units = new();
        if (root.Valid)
        {
            CollectAnnotations(root, units);
            return new JsonSchemaEvaluationResult(root, JsonSchemaOutputFormat.Basic, keepAnnotation: false, _none, units);
        }

        CollectErrors(root, units);
        return new JsonSchemaEvaluationResult(root, JsonSchemaOutputFormat.Basic, keepAnnotation: false, units, _none);
    }

    private static void CollectErrors(OutputUnit unit, List<JsonSchemaEvaluationResult> units)
    {
        if (unit.Valid)
        {
            return;
        }

        if (unit.Error is not null)
        {
            units.Add(new JsonSchemaEvaluationResult(unit, JsonSchemaOutputFormat.Basic, keepAnnotation: false, _none, _none));
        }

        if (unit.Children is not null)
        {
            foreach (OutputUnit child in unit.Children)
            {
                CollectErrors(child, units);
            }
        }
    }

    private static void CollectAnnotations(OutputUnit unit, List<JsonSchemaEvaluationResult> units)
    {
        if (!unit.Valid || unit.DiscardsAnnotations)
        {
            return;
        }

        if (unit.Annotation is not null)
        {
            units.Add(new JsonSchemaEvaluationResult(unit, JsonSchemaOutputFormat.Basic, keepAnnotation: true, _none, _none));
        }

        if (unit.Children is not null)
        {
            foreach (OutputUnit child in unit.Children)
            {
                CollectAnnotations(child, units);
            }
        }
    }
}
