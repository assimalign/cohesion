using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>contentMediaType</c> (2020-12 Validation §8.4): an annotation on string instances, and, when the caller sets
/// <see cref="JsonSchemaEvaluationOptions.ContentAssertion"/>, an assertion that JSON media types
/// (<c>application/json</c> and <c>+json</c> suffixes) parse after the sibling encoding is decoded.
/// </summary>
internal sealed class ContentMediaTypeKeyword : SchemaKeyword
{
    public ContentMediaTypeKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
        MediaType = value.GetString()!;
        ReadOnlySpan<char> type = MediaType.AsSpan();
        int parameters = type.IndexOf(';');
        if (parameters >= 0)
        {
            type = type.Slice(0, parameters);
        }

        type = type.Trim();
        IsJson = type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    public string MediaType { get; }

    public bool IsJson { get; }

    public ContentEncodingKeyword? Encoding { get; private set; }

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            if (sibling is ContentEncodingKeyword encoding)
            {
                Encoding = encoding;
            }
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        context.Annotate(Value);
        if (!IsJson || !context.Options.ContentAssertion)
        {
            return true;
        }

        // Content that does not decode is the encoding keyword's failure, not this one's.
        if (!TryDecode(instance, Encoding, out byte[] content))
        {
            return true;
        }

        if (TryParse(content, out JsonDocument? document))
        {
            document.Dispose();
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.ContentMediaTypeInvalid, MediaType));
        }

        return false;
    }

    /// <summary>Decodes string content per its <c>contentEncoding</c>: base64, or the string's UTF-8 when there is none.</summary>
    public static bool TryDecode(JsonElement instance, ContentEncodingKeyword? encoding, out byte[] content)
    {
        string text = instance.GetString()!;
        if (encoding is null)
        {
            content = System.Text.Encoding.UTF8.GetBytes(text);
            return true;
        }

        if (!encoding.IsBase64)
        {
            content = [];
            return false;
        }

        try
        {
            content = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            content = [];
            return false;
        }
    }

    public static bool TryParse(byte[] content, [NotNullWhen(true)] out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }
}
