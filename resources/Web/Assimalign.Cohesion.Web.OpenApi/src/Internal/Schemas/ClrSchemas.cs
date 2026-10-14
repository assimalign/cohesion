using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Schemas for values the request carries as text (route values, query strings, headers and form fields)
/// and for the files it uploads.
/// </summary>
/// <remarks>
/// <para>
/// The Web.Api thunk binds text values with <c>IParsable&lt;T&gt;.TryParse</c> under the invariant culture
/// (enums with <c>Enum.TryParse</c>), never through System.Text.Json, so their schemas come from the declared
/// CLR type alone: the type the source generator recorded with <c>typeof(...)</c>, matched against a
/// fixed table. Nothing is reflected over except an enum's names, read with <see cref="Enum.GetNames(Type)"/>,
/// which NativeAOT keeps for every enum a program uses. A file parameter is matched against the Http.Forms
/// file types the same way.
/// </para>
/// <para>
/// A route parameter with no recorded type (an endpoint mapped with plain middleware) is typed from its
/// inline constraint instead (<c>{id:int}</c>), and every constraint that bounds a value
/// (<c>min</c>, <c>range</c>, <c>length</c>, <c>regex</c>, …) refines the schema either way.
/// </para>
/// </remarks>
internal static class ClrSchemas
{
    /// <summary>
    /// Gets the OpenAPI format of a numeric CLR type (format registry: <c>int32</c>, <c>int64</c>,
    /// <c>double</c>, …).
    /// </summary>
    /// <param name="type">The CLR type, already unwrapped from <see cref="Nullable{T}"/>.</param>
    /// <param name="format">The format, or <see langword="null"/> for a numeric type the registry has no format for.</param>
    /// <returns><see langword="true"/> when <paramref name="type"/> is a numeric type.</returns>
    public static bool TryGetNumericFormat(Type type, out string? format)
    {
        format = Type.GetTypeCode(type) switch
        {
            TypeCode.SByte => "int8",
            TypeCode.Byte => "uint8",
            TypeCode.Int16 => "int16",
            TypeCode.UInt16 => "uint16",
            TypeCode.Int32 => "int32",
            TypeCode.UInt32 => "uint32",
            TypeCode.Int64 => "int64",
            TypeCode.UInt64 => "uint64",
            TypeCode.Single => "float",
            TypeCode.Double => "double",
            TypeCode.Decimal => "decimal",
            _ => null
        };

        if (type.IsEnum)
        {
            format = null;
            return false;
        }

        return format is not null || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Half);
    }

    /// <summary>
    /// Describes a value bound from request text with the given declared type.
    /// </summary>
    /// <param name="type">The declared CLR type, possibly <see cref="Nullable{T}"/>.</param>
    /// <returns>The schema. Optionality is the parameter's <c>required</c>, not a null type.</returns>
    public static OpenApiSchema ForText(Type type)
    {
        Type value = Nullable.GetUnderlyingType(type) ?? type;

        if (value == typeof(string))
        {
            return new OpenApiSchema { Type = SchemaType.String };
        }

        if (value == typeof(bool))
        {
            return new OpenApiSchema { Type = SchemaType.Boolean };
        }

        if (value.IsEnum)
        {
            // Enum.TryParse accepts the member names (and their numeric values); the names document it.
            OpenApiSchema enumeration = new() { Type = SchemaType.String };

            foreach (string name in Enum.GetNames(value))
            {
                enumeration.Enum.Add(name);
            }

            return enumeration;
        }

        if (TryGetNumericFormat(value, out string? format))
        {
            bool integral = Type.GetTypeCode(value) is >= TypeCode.SByte and <= TypeCode.UInt64
                || value == typeof(Int128)
                || value == typeof(UInt128);

            return new OpenApiSchema { Type = integral ? SchemaType.Integer : SchemaType.Number, Format = format };
        }

        if (value == typeof(char))
        {
            return new OpenApiSchema { Type = SchemaType.String, MinLength = 1, MaxLength = 1 };
        }

        string? textFormat = null;

        if (value == typeof(Guid))
        {
            textFormat = "uuid";
        }
        else if (value == typeof(DateTime) || value == typeof(DateTimeOffset))
        {
            textFormat = "date-time";
        }
        else if (value == typeof(DateOnly))
        {
            textFormat = "date";
        }
        else if (value == typeof(TimeOnly))
        {
            textFormat = "time";
        }
        else if (value == typeof(Uri))
        {
            textFormat = "uri";
        }

        // Any other IParsable<T> is text the type parses.
        return new OpenApiSchema { Type = SchemaType.String, Format = textFormat };
    }

    /// <summary>
    /// Describes an uploaded-file parameter as a part of a <c>multipart/form-data</c> body: a binary string
    /// for an <see cref="IHttpFormFile"/>, and an array of them for a file sequence or an
    /// <see cref="IHttpFormFileCollection"/>, which may each hold any number of files.
    /// </summary>
    /// <remarks>
    /// The schema is the same on every OpenAPI line: <c>type: string</c> with the registry format
    /// <c>binary</c> ("any sequence of octets"), which OpenAPI 3.0 defines as an
    /// <c>application/octet-stream</c> part and which client generators also read as a file in 3.1 and 3.2
    /// documents, where JSON Schema treats <c>format</c> as an annotation.
    /// </remarks>
    /// <param name="type">
    /// The declared CLR type, which Web.Api restricts to <see cref="IHttpFormFile"/>, a sequence of it, or
    /// <see cref="IHttpFormFileCollection"/>.
    /// </param>
    /// <returns>The schema. Optionality is the part's entry in the body's <c>required</c>, not a null type.</returns>
    public static OpenApiSchema ForFile(Type type)
    {
        OpenApiSchema file = new() { Type = SchemaType.String, Format = "binary" };

        return type == typeof(IHttpFormFile)
            ? file
            : new OpenApiSchema { Type = SchemaType.Array, Items = file };
    }

    /// <summary>
    /// Describes a route parameter: from its declared type when the endpoint recorded one, otherwise from
    /// its inline type constraint, then refined by its bounding constraints.
    /// </summary>
    /// <param name="parameter">The route template's parameter.</param>
    /// <param name="declaredType">The handler parameter's declared type, or <see langword="null"/>.</param>
    /// <param name="defaultValue">The template's default for the parameter, or <see langword="null"/>.</param>
    /// <returns>The schema.</returns>
    public static OpenApiSchema ForRouteParameter(RoutePatternParameterSegment parameter, Type? declaredType, object? defaultValue)
    {
        OpenApiSchema schema = declaredType is not null ? ForText(declaredType) : ForConstraints(parameter);

        foreach (RoutePatternParameterPolicyReference policy in parameter.ParameterPolicies)
        {
            if (policy.Content is { } content && TrySplitPolicy(content, out string name, out string? argument))
            {
                ApplyBound(schema, name, argument);
            }
        }

        if (defaultValue is not null && ToDefault(defaultValue, schema) is { } value)
        {
            schema.Default = value;
        }

        return schema;
    }

    private static OpenApiSchema ForConstraints(RoutePatternParameterSegment parameter)
    {
        foreach (RoutePatternParameterPolicyReference policy in parameter.ParameterPolicies)
        {
            if (policy.Content is not { } content || !TrySplitPolicy(content, out string name, out _))
            {
                continue;
            }

            Type? constrained = name.ToLowerInvariant() switch
            {
                "int" => typeof(int),
                "long" => typeof(long),
                "decimal" => typeof(decimal),
                "double" => typeof(double),
                "float" => typeof(float),
                "bool" => typeof(bool),
                "guid" => typeof(Guid),
                "datetime" => typeof(DateTime),
                _ => null
            };

            if (constrained is not null)
            {
                return ForText(constrained);
            }
        }

        return new OpenApiSchema { Type = SchemaType.String };
    }

    private static void ApplyBound(OpenApiSchema schema, string name, string? argument)
    {
        switch (name.ToLowerInvariant())
        {
            case "alpha":
                schema.Pattern ??= "^[A-Za-z]+$";
                break;
            case "regex" when !string.IsNullOrEmpty(argument):
                schema.Pattern = argument;
                break;
            case "min" when TryParseNumber(argument, out double minimum):
                schema.Minimum = minimum;
                break;
            case "max" when TryParseNumber(argument, out double maximum):
                schema.Maximum = maximum;
                break;
            case "range" when TrySplitPair(argument, out string? low, out string? high)
                && TryParseNumber(low, out double rangeMinimum)
                && TryParseNumber(high, out double rangeMaximum):
                schema.Minimum = rangeMinimum;
                schema.Maximum = rangeMaximum;
                break;
            case "minlength" when TryParseLength(argument, out int minimumLength):
                schema.MinLength = minimumLength;
                break;
            case "maxlength" when TryParseLength(argument, out int maximumLength):
                schema.MaxLength = maximumLength;
                break;
            case "length" when TrySplitPair(argument, out string? shortest, out string? longest):
                if (TryParseLength(shortest, out int lengthMinimum) && TryParseLength(longest, out int lengthMaximum))
                {
                    schema.MinLength = lengthMinimum;
                    schema.MaxLength = lengthMaximum;
                }
                break;
            case "length" when TryParseLength(argument, out int exactLength):
                schema.MinLength = exactLength;
                schema.MaxLength = exactLength;
                break;
        }
    }

    // "name" or "name(argument)": the inline policy text the route template carries.
    private static bool TrySplitPolicy(string content, out string name, out string? argument)
    {
        int open = content.IndexOf('(');

        if (open < 0)
        {
            name = content.Trim();
            argument = null;
            return name.Length > 0;
        }

        name = content[..open].Trim();
        argument = content.EndsWith(')') ? content[(open + 1)..^1] : null;
        return name.Length > 0;
    }

    private static bool TrySplitPair(string? argument, [NotNullWhen(true)] out string? first, [NotNullWhen(true)] out string? second)
    {
        int comma = argument?.IndexOf(',') ?? -1;

        if (argument is null || comma < 0)
        {
            first = null;
            second = null;
            return false;
        }

        first = argument[..comma];
        second = argument[(comma + 1)..];
        return true;
    }

    private static bool TryParseNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseLength(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;

    // A template default is text (or a value its typed constraint produced); the schema's default must
    // conform to the schema's type, so it is converted to that type and dropped when it does not fit.
    private static OpenApiNode? ToDefault(object value, OpenApiSchema schema)
    {
        string? text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

        if (text is null)
        {
            return null;
        }

        return schema.Type switch
        {
            SchemaType.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer) ? integer : null,
            SchemaType.Number => TryParseNumber(text, out double number) ? number : null,
            SchemaType.Boolean => bool.TryParse(text, out bool flag) ? flag : null,
            _ => text
        };
    }
}
