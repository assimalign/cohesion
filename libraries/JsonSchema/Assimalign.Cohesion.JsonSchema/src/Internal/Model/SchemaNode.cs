using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// One compiled schema object or boolean schema, and the facade it presents as an
/// <see cref="IJsonSchema"/>.
/// </summary>
[DebuggerDisplay("{Pointer,nq} in {Resource.BaseText,nq}")]
internal sealed class SchemaNode : IJsonSchema
{
    private static readonly SchemaKeyword[] _noKeywords = [];

    public SchemaNode(SchemaResource resource, string pointer, string relativePath, JsonElement element)
    {
        Resource = resource;
        Pointer = pointer;
        RelativePath = relativePath;
        Element = element;
        BooleanValue = element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public SchemaResource Resource { get; }

    /// <summary>Gets the JSON Pointer of the schema within its document.</summary>
    public string Pointer { get; }

    /// <summary>
    /// Gets the pointer suffix from the schema object that holds this subschema, such as
    /// <c>/properties/name</c>; empty for a document root or a schema compiled on demand.
    /// </summary>
    public string RelativePath { get; }

    public JsonElement Element { get; }

    /// <summary>Gets the value of a boolean schema, or <see langword="null"/> for a schema object.</summary>
    public bool? BooleanValue { get; }

    /// <summary>Gets every keyword with an evaluator, in evaluation order.</summary>
    public SchemaKeyword[] Keywords { get; private set; } = _noKeywords;

    /// <summary>Gets the keywords that can fail, in evaluation order: <see cref="Keywords"/> without pure annotations.</summary>
    public SchemaKeyword[] AssertingKeywords { get; private set; } = _noKeywords;

    /// <summary>Gets a value indicating whether the schema holds <c>unevaluatedProperties</c> or <c>unevaluatedItems</c>.</summary>
    public bool HasUnevaluated { get; private set; }

    public bool IsResourceRoot => ReferenceEquals(Resource.Root, this);

    public Uri? BaseUri => Resource.Uri;

    public JsonPointer Location => JsonPointer.CreateUnchecked(Pointer);

    public JsonSchemaDialect Dialect => Resource.Dialect;

    public JsonElement Source => Element;

    public JsonSchemaDocument Document => Resource.Document;

    public JsonSchemaEvaluationResult Evaluate(JsonElement instance, JsonSchemaEvaluationOptions? options = null) =>
        EvaluationContext.Evaluate(this, instance, options ?? JsonSchemaEvaluationOptions.Default);

    public bool IsValid(JsonElement instance, JsonSchemaEvaluationOptions? options = null) =>
        EvaluationContext.IsValid(this, instance, options ?? JsonSchemaEvaluationOptions.Default);

    /// <summary>
    /// Links keywords that read their siblings, then orders them for evaluation. Keywords of equal
    /// priority keep their document order.
    /// </summary>
    public void Initialize(List<SchemaKeyword> keywords)
    {
        foreach (SchemaKeyword keyword in keywords)
        {
            keyword.Link(keywords);
        }

        SchemaKeyword[] ordered = keywords.ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            SchemaKeyword current = ordered[i];
            int j = i - 1;
            while (j >= 0 && ordered[j].Priority > current.Priority)
            {
                ordered[j + 1] = ordered[j];
                j--;
            }

            ordered[j + 1] = current;
        }

        int asserting = 0;
        bool hasUnevaluated = false;
        foreach (SchemaKeyword keyword in ordered)
        {
            if (!keyword.IsAnnotationOnly)
            {
                asserting++;
            }

            hasUnevaluated |= keyword is UnevaluatedPropertiesKeyword or UnevaluatedItemsKeyword;
        }

        SchemaKeyword[] assertingKeywords = asserting == ordered.Length ? ordered : new SchemaKeyword[asserting];
        if (!ReferenceEquals(assertingKeywords, ordered))
        {
            int index = 0;
            foreach (SchemaKeyword keyword in ordered)
            {
                if (!keyword.IsAnnotationOnly)
                {
                    assertingKeywords[index++] = keyword;
                }
            }
        }

        Keywords = ordered.Length == 0 ? _noKeywords : ordered;
        AssertingKeywords = assertingKeywords.Length == 0 ? _noKeywords : assertingKeywords;
        HasUnevaluated = hasUnevaluated;
    }

    /// <summary>
    /// Returns the canonical URI of this schema, or of one of its keywords, or <see langword="null"/>
    /// when its resource has no identifier.
    /// </summary>
    public string? GetAbsoluteLocation(SchemaKeyword? keyword)
    {
        if (Resource.IsSynthetic)
        {
            return null;
        }

        string pointer = Pointer.Substring(Resource.DocumentPointer.Length);
        if (keyword is not null)
        {
            pointer = pointer + "/" + JsonPointer.EscapeToken(keyword.Name);
        }

        return Resource.BaseText + "#" + UriReference.EscapeFragment(pointer);
    }
}
