using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>json-pointer</c> format (RFC 6901 §3) and the <c>relative-json-pointer</c> format.
/// </summary>
/// <remarks>
/// A relative JSON pointer is a non-negative integer without leading zeros followed by a JSON pointer
/// or <c>#</c> (draft-handrews-relative-json-pointer-01). Draft 2020-12 cites
/// draft-bhutton-relative-json-pointer-00, which adds an index manipulation (<c>+</c> or <c>-</c> and
/// a second integer) after the first, so it is accepted in that dialect only.
/// </remarks>
internal static class JsonPointerFormat
{
    /// <summary>RFC 6901 <c>json-pointer</c>: empty, or <c>/</c>-prefixed tokens whose <c>~</c> escapes are <c>~0</c> or <c>~1</c>.</summary>
    public static bool IsJsonPointer(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsPointer(value);

    public static bool IsRelativeJsonPointer(ReadOnlySpan<char> value, JsonSchemaVersion version)
    {
        int position = ReadNonNegativeInteger(value, 0);
        if (position < 0)
        {
            return false;
        }

        if (version >= JsonSchemaVersion.Draft202012 && position < value.Length && value[position] is '+' or '-')
        {
            position = ReadNonNegativeInteger(value, position + 1);
            if (position < 0)
            {
                return false;
            }
        }

        if (position == value.Length)
        {
            return true;
        }

        if (value[position] == '#')
        {
            return position + 1 == value.Length;
        }

        return value[position] == '/' && IsPointer(value.Slice(position));
    }

    private static bool IsPointer(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        if (value[0] != '/')
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] == '~' && (i + 1 == value.Length || value[i + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads <c>"0" / %x31-39 *DIGIT</c> and returns the position after it, or -1.</summary>
    private static int ReadNonNegativeInteger(ReadOnlySpan<char> value, int position)
    {
        if (position >= value.Length || !char.IsAsciiDigit(value[position]))
        {
            return -1;
        }

        if (value[position] == '0')
        {
            return position + 1;
        }

        while (position < value.Length && char.IsAsciiDigit(value[position]))
        {
            position++;
        }

        return position;
    }
}
