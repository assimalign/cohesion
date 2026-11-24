using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>type</c> (2020-12 Validation §6.1.1): the instance must be one of the named primitive types.
/// </summary>
/// <remarks>
/// <c>integer</c> means a number with no fractional part, so <c>1.0</c> is an integer. Draft 4 judged
/// integers by their text, so there <c>1.0</c> is not.
/// </remarks>
internal sealed class TypeKeyword : SchemaKeyword
{
    private const int nullType = 1;
    private const int booleanType = 2;
    private const int objectType = 4;
    private const int arrayType = 8;
    private const int numberType = 16;
    private const int stringType = 32;
    private const int integerType = 64;

    private TypeKeyword(SchemaNode owner, string name, JsonElement value, int allowed, string expected, bool draft04)
        : base(owner, name, value)
    {
        Allowed = allowed;
        Expected = expected;
        Draft04 = draft04;
    }

    /// <summary>Gets the allowed types as flags.</summary>
    public int Allowed { get; }

    /// <summary>Gets the allowed type names, for errors.</summary>
    public string Expected { get; }

    public bool Draft04 { get; }

    public static TypeKeyword Create(SchemaNode owner, string name, JsonElement value, bool draft04, string location)
    {
        int allowed = 0;
        List<string> names = new();
        if (value.ValueKind == JsonValueKind.String)
        {
            allowed = Parse(value.GetString()!, name, location);
            names.Add(value.GetString()!);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw JsonSchemaProcessingException.KeywordValueInvalid(name, location);
                }

                allowed |= Parse(item.GetString()!, name, location);
                names.Add(item.GetString()!);
            }
        }
        else
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(name, location);
        }

        return new TypeKeyword(owner, name, value, allowed, string.Join(", ", names), draft04);
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        int actual = instance.ValueKind switch
        {
            JsonValueKind.Null => nullType,
            JsonValueKind.True or JsonValueKind.False => booleanType,
            JsonValueKind.Object => objectType,
            JsonValueKind.Array => arrayType,
            JsonValueKind.Number => numberType,
            JsonValueKind.String => stringType,
            _ => 0,
        };

        if ((Allowed & actual) != 0 || (actual == numberType && (Allowed & integerType) != 0 && IsInteger(instance)))
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            string found = actual == numberType && IsInteger(instance) ? "integer" : GetName(actual);
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.TypeMismatch, Expected, found));
        }

        return false;
    }

    private static int Parse(string type, string keyword, string location) => type switch
    {
        "null" => nullType,
        "boolean" => booleanType,
        "object" => objectType,
        "array" => arrayType,
        "number" => numberType,
        "string" => stringType,
        "integer" => integerType,
        _ => throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location),
    };

    private static string GetName(int type) => type switch
    {
        nullType => "null",
        booleanType => "boolean",
        objectType => "object",
        arrayType => "array",
        numberType => "number",
        _ => "string",
    };

    private bool IsInteger(JsonElement instance)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(instance);
        if (raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0)
        {
            return true;
        }

        return !Draft04 && JsonNumber.TryParse(raw, out JsonNumber number) && number.IsInteger;
    }
}
