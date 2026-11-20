using System;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <see cref="IJsonSchemaKeywordContext"/> a custom keyword evaluates with: a view of the
/// evaluation positioned at the keyword, valid until its <see cref="IJsonSchemaKeyword.Evaluate"/>
/// returns.
/// </summary>
internal sealed class KeywordContext : IJsonSchemaKeywordContext
{
    public KeywordContext(EvaluationContext evaluation, CustomKeyword compiled, JsonElement instance)
    {
        Evaluation = evaluation;
        Compiled = compiled;
        Instance = instance;
    }

    public EvaluationContext Evaluation { get; }

    /// <summary>Gets the keyword being evaluated.</summary>
    public CustomKeyword Compiled { get; }

    public JsonElement Instance { get; }

    public bool Failed { get; private set; }

    public string? Message { get; private set; }

    public JsonElement? Annotation { get; private set; }

    /// <summary>Gets a value indicating whether the keyword's evaluation has returned.</summary>
    public bool IsClosed { get; private set; }

    public JsonPointer InstanceLocation
    {
        get
        {
            ThrowIfClosed();
            return Evaluation.GetInstanceLocation();
        }
    }

    public string Keyword => Compiled.Name;

    public JsonElement KeywordValue => Compiled.Value;

    public IJsonSchema Schema => Compiled.Owner;

    public JsonSchemaDialect Dialect => Compiled.Owner.Dialect;

    public JsonSchemaEvaluationOptions Options => Evaluation.Options;

    public void Fail(string? message = null)
    {
        ThrowIfClosed();
        Failed = true;
        Message = message;
    }

    public void Annotate(JsonElement annotation)
    {
        ThrowIfClosed();
        if (annotation.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(ErrorMessages.AnnotationUndefined, nameof(annotation));
        }

        if (Evaluation.RecordsOutput)
        {
            Annotation = annotation.Clone();
        }
    }

    public bool EvaluateSubschema(JsonPointer subschema)
    {
        SchemaNode node = FindSubschema(subschema);
        return Evaluation.EvaluateInPlace(node, Instance);
    }

    public bool EvaluateSubschema(JsonPointer subschema, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        SchemaNode node = FindSubschema(subschema);
        if (Instance.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty member in Instance.EnumerateObject())
            {
                if (member.NameEquals(propertyName))
                {
                    return Evaluation.EvaluateProperty(node, member);
                }
            }
        }

        throw new ArgumentException(ErrorMessages.InstanceMemberNotFound, nameof(propertyName));
    }

    public bool EvaluateSubschema(JsonPointer subschema, int index)
    {
        SchemaNode node = FindSubschema(subschema);
        if (Instance.ValueKind != JsonValueKind.Array || index < 0 || index >= Instance.GetArrayLength())
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, ErrorMessages.InstanceItemNotFound);
        }

        return Evaluation.EvaluateItem(node, Instance[index], index);
    }

    public void MarkPropertyEvaluated(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ThrowIfClosed();
        if (Instance.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(ErrorMessages.InstanceMemberNotFound, nameof(propertyName));
        }

        bool found = false;
        int position = 0;
        foreach (JsonProperty member in Instance.EnumerateObject())
        {
            if (member.NameEquals(propertyName))
            {
                Evaluation.MarkProperty(position);
                found = true;
            }

            position++;
        }

        if (!found)
        {
            throw new ArgumentException(ErrorMessages.InstanceMemberNotFound, nameof(propertyName));
        }
    }

    public void MarkItemEvaluated(int index)
    {
        ThrowIfClosed();
        if (Instance.ValueKind != JsonValueKind.Array || index < 0 || index >= Instance.GetArrayLength())
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, ErrorMessages.InstanceItemNotFound);
        }

        Evaluation.MarkItem(index);
    }

    /// <summary>Ends the keyword's access to the evaluation.</summary>
    public void Close() => IsClosed = true;

    private SchemaNode FindSubschema(JsonPointer subschema)
    {
        ThrowIfClosed();
        if (subschema.Value is null)
        {
            throw new ArgumentException(ErrorMessages.JsonPointerDefault, nameof(subschema));
        }

        if (!Compiled.Subschemas.TryGetValue(subschema.Value, out SchemaNode? node))
        {
            throw new ArgumentException(ErrorMessages.SubschemaNotFound, nameof(subschema));
        }

        return node;
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException(ErrorMessages.KeywordContextClosed);
        }
    }
}
