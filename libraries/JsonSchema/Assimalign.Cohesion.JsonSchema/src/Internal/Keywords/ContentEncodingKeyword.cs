using System;
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>contentEncoding</c> (2020-12 Validation §8.3): an annotation on string instances, and, when the caller sets
/// <see cref="JsonSchemaEvaluationOptions.ContentAssertion"/>, an assertion that <c>base64</c> content
/// decodes. Other encodings are not checked.
/// </summary>
internal sealed class ContentEncodingKeyword : SchemaKeyword
{
    public ContentEncodingKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
        Encoding = value.GetString()!;
        IsBase64 = string.Equals(Encoding, "base64", StringComparison.OrdinalIgnoreCase);
    }

    public string Encoding { get; }

    public bool IsBase64 { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        context.Annotate(Value);
        if (!IsBase64 || !context.Options.ContentAssertion)
        {
            return true;
        }

        if (Base64.IsValid(instance.GetString().AsSpan()))
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.ContentEncodingInvalid, Encoding));
        }

        return false;
    }
}
