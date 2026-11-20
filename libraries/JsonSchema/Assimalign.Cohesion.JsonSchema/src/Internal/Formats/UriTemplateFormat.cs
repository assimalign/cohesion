using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>uri-template</c> format: an RFC 6570 <c>URI-Template</c> at any level.
/// </summary>
/// <remarks>
/// <para>
/// Literals are any <c>ucschar</c> or <c>iprivate</c> character, a percent escape, or an ASCII
/// character other than controls, space, <c>"</c>, <c>%</c>, <c>&lt;</c>, <c>&gt;</c>, <c>\</c>,
/// <c>^</c>, <c>`</c>, <c>{</c>, <c>|</c> and <c>}</c>. The apostrophe is a literal, as RFC 6570
/// errata 6937 corrects the grammar to say: it is a <c>sub-delim</c> in RFC 3986.
/// </para>
/// <para>
/// An expression is <c>"{" [ operator ] variable-list "}"</c>, where a variable name is dotted
/// <c>varchar</c> runs (<c>ALPHA / DIGIT / "_" / pct-encoded</c>), and each name takes at most one
/// modifier: <c>*</c>, or <c>:</c> and a length from 1 to 9999 without leading zeros. The reserved
/// operators <c>= , ! @ |</c> are part of the grammar and accepted.
/// </para>
/// </remarks>
internal static class UriTemplateFormat
{
    private static readonly SearchValues<char> _literals = SearchValues.Create(
        "!#$&'()*+,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_abcdefghijklmnopqrstuvwxyz~");

    private static readonly SearchValues<char> _operators = SearchValues.Create("+#./;?&=,!@|");

    public static bool IsUriTemplate(ReadOnlySpan<char> value, JsonSchemaVersion version)
    {
        int position = 0;
        while (position < value.Length)
        {
            char c = value[position];
            if (c == '{')
            {
                int length = value.Slice(position + 1).IndexOf('}');
                if (length < 0 || !IsExpression(value.Slice(position + 1, length)))
                {
                    return false;
                }

                position += length + 2;
                continue;
            }

            if (c == '%')
            {
                if (!IsPercentEncoded(value, position))
                {
                    return false;
                }

                position += 3;
                continue;
            }

            if (c < 0x80)
            {
                if (!_literals.Contains(c))
                {
                    return false;
                }

                position++;
                continue;
            }

            if (Rune.DecodeFromUtf16(value.Slice(position), out Rune rune, out int consumed) != OperationStatus.Done
                || !(UriFormat.IsUcsChar(rune.Value) || UriFormat.IsIPrivate(rune.Value)))
            {
                return false;
            }

            position += consumed;
        }

        return true;
    }

    private static bool IsExpression(ReadOnlySpan<char> expression)
    {
        int position = !expression.IsEmpty && _operators.Contains(expression[0]) ? 1 : 0;
        while (true)
        {
            // varname = varchar *( ["."] varchar )
            if (!TryReadVarchar(expression, ref position))
            {
                return false;
            }

            while (position < expression.Length)
            {
                if (expression[position] == '.')
                {
                    position++;
                    if (!TryReadVarchar(expression, ref position))
                    {
                        return false;
                    }
                }
                else if (!TryReadVarchar(expression, ref position))
                {
                    break;
                }
            }

            if (position < expression.Length && expression[position] == ':')
            {
                // max-length = %x31-39 0*3DIGIT
                position++;
                int start = position;
                if (position >= expression.Length || expression[position] is < '1' or > '9')
                {
                    return false;
                }

                while (position < expression.Length && char.IsAsciiDigit(expression[position]))
                {
                    position++;
                }

                if (position - start > 4)
                {
                    return false;
                }
            }
            else if (position < expression.Length && expression[position] == '*')
            {
                position++;
            }

            if (position == expression.Length)
            {
                return true;
            }

            if (expression[position] != ',')
            {
                return false;
            }

            position++;
        }
    }

    /// <summary>Reads one <c>varchar</c>: <c>ALPHA / DIGIT / "_" / pct-encoded</c>.</summary>
    private static bool TryReadVarchar(ReadOnlySpan<char> value, ref int position)
    {
        if (position >= value.Length)
        {
            return false;
        }

        char c = value[position];
        if (char.IsAsciiLetterOrDigit(c) || c == '_')
        {
            position++;
            return true;
        }

        if (c == '%' && IsPercentEncoded(value, position))
        {
            position += 3;
            return true;
        }

        return false;
    }

    private static bool IsPercentEncoded(ReadOnlySpan<char> value, int position) =>
        position + 2 < value.Length && char.IsAsciiHexDigit(value[position + 1]) && char.IsAsciiHexDigit(value[position + 2]);
}
