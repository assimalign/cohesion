using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The RFC 9651 &#167; 4.2 parser. A single forward pass over a <see cref="ReadOnlySpan{Char}"/>,
/// implementing strict fail-parsing: any malformed or out-of-range input fails the whole
/// field. Tokenization is span-based; only the materialized values (strings, byte arrays,
/// and the result collections) allocate.
/// </summary>
internal ref struct StructuredFieldParser
{
    private readonly ReadOnlySpan<char> _input;
    private int _pos;
    private string? _error;

    private StructuredFieldParser(ReadOnlySpan<char> input)
    {
        _input = input;
        _pos = 0;
        _error = null;
    }

    private readonly bool Eof => _pos >= _input.Length;

    private readonly char Current => _input[_pos];

    private bool Fail(string message)
    {
        _error ??= message;
        return false;
    }

    private void SkipSp()
    {
        while (_pos < _input.Length && _input[_pos] == ' ')
        {
            _pos++;
        }
    }

    private void SkipOws()
    {
        while (_pos < _input.Length && (_input[_pos] == ' ' || _input[_pos] == '\t'))
        {
            _pos++;
        }
    }

    // ------------------------------------------------------------------
    // Top-level entry points (RFC 9651 §4.2)
    // ------------------------------------------------------------------

    internal static bool TryParseItem(ReadOnlySpan<char> input, out StructuredFieldItem result, out string? error)
    {
        var parser = new StructuredFieldParser(input);
        bool ok = parser.ParseItemField(out result);
        error = ok ? null : parser._error ?? "Malformed structured field item.";
        return ok;
    }

    internal static bool TryParseList(ReadOnlySpan<char> input, out StructuredFieldList result, out string? error)
    {
        var parser = new StructuredFieldParser(input);
        bool ok = parser.ParseListField(out result);
        error = ok ? null : parser._error ?? "Malformed structured field list.";
        return ok;
    }

    internal static bool TryParseDictionary(ReadOnlySpan<char> input, out StructuredFieldDictionary result, out string? error)
    {
        var parser = new StructuredFieldParser(input);
        bool ok = parser.ParseDictionaryField(out result);
        error = ok ? null : parser._error ?? "Malformed structured field dictionary.";
        return ok;
    }

    private bool ParseItemField(out StructuredFieldItem result)
    {
        result = default;
        SkipSp();
        if (!ParseItem(out StructuredFieldItem item))
        {
            return false;
        }
        SkipSp();
        if (!Eof)
        {
            return Fail("Unexpected trailing characters after item.");
        }
        result = item;
        return true;
    }

    private bool ParseListField(out StructuredFieldList result)
    {
        result = default;
        SkipSp();
        if (!ParseListMembers(out StructuredFieldMember[] members))
        {
            return false;
        }
        SkipSp();
        if (!Eof)
        {
            return Fail("Unexpected trailing characters after list.");
        }
        result = StructuredFieldList.CreateRaw(members);
        return true;
    }

    private bool ParseDictionaryField(out StructuredFieldDictionary result)
    {
        result = default;
        SkipSp();
        if (!ParseDictionaryMembers(out KeyValuePair<string, StructuredFieldMember>[] members))
        {
            return false;
        }
        SkipSp();
        if (!Eof)
        {
            return Fail("Unexpected trailing characters after dictionary.");
        }
        result = StructuredFieldDictionary.CreateRaw(members);
        return true;
    }

    // ------------------------------------------------------------------
    // List (RFC 9651 §4.2.1)
    // ------------------------------------------------------------------

    private bool ParseListMembers(out StructuredFieldMember[] members)
    {
        members = Array.Empty<StructuredFieldMember>();
        var list = new List<StructuredFieldMember>();
        while (!Eof)
        {
            if (!ParseItemOrInnerList(out StructuredFieldMember member))
            {
                return false;
            }
            list.Add(member);
            SkipOws();
            if (Eof)
            {
                break;
            }
            if (Current != ',')
            {
                return Fail("Expected ',' separating list members.");
            }
            _pos++; // consume ','
            SkipOws();
            if (Eof)
            {
                return Fail("Trailing comma in list.");
            }
        }
        members = list.ToArray();
        return true;
    }

    private bool ParseItemOrInnerList(out StructuredFieldMember member)
    {
        member = default;
        if (!Eof && Current == '(')
        {
            if (!ParseInnerList(out StructuredFieldInnerList innerList))
            {
                return false;
            }
            member = StructuredFieldMember.FromInnerList(innerList);
            return true;
        }
        if (!ParseItem(out StructuredFieldItem item))
        {
            return false;
        }
        member = StructuredFieldMember.FromItem(item);
        return true;
    }

    // ------------------------------------------------------------------
    // Inner list (RFC 9651 §4.2.1.2)
    // ------------------------------------------------------------------

    private bool ParseInnerList(out StructuredFieldInnerList result)
    {
        result = default;
        _pos++; // consume '('
        var items = new List<StructuredFieldItem>();
        while (!Eof)
        {
            SkipSp();
            if (!Eof && Current == ')')
            {
                _pos++; // consume ')'
                if (!ParseParameters(out StructuredFieldParameters innerParameters))
                {
                    return false;
                }
                result = StructuredFieldInnerList.CreateRaw(items.ToArray(), innerParameters);
                return true;
            }
            if (!ParseItem(out StructuredFieldItem item))
            {
                return false;
            }
            items.Add(item);
            if (!Eof && Current != ' ' && Current != ')')
            {
                return Fail("Inner list items must be separated by a space.");
            }
        }
        return Fail("Unterminated inner list.");
    }

    // ------------------------------------------------------------------
    // Dictionary (RFC 9651 §4.2.2)
    // ------------------------------------------------------------------

    private bool ParseDictionaryMembers(out KeyValuePair<string, StructuredFieldMember>[] members)
    {
        members = Array.Empty<KeyValuePair<string, StructuredFieldMember>>();
        var list = new List<KeyValuePair<string, StructuredFieldMember>>();
        while (!Eof)
        {
            if (!ParseKey(out string key))
            {
                return false;
            }

            StructuredFieldMember member;
            if (!Eof && Current == '=')
            {
                _pos++; // consume '='
                if (!ParseItemOrInnerList(out member))
                {
                    return false;
                }
            }
            else
            {
                // A key with no "=" has the value Boolean true, but may still carry parameters.
                if (!ParseParameters(out StructuredFieldParameters parameters))
                {
                    return false;
                }
                member = StructuredFieldMember.FromItem(
                    new StructuredFieldItem(StructuredFieldBareItem.CreateBoolean(true), parameters));
            }

            StructuredFieldDictionary.Upsert(list, key, member);

            SkipOws();
            if (Eof)
            {
                break;
            }
            if (Current != ',')
            {
                return Fail("Expected ',' separating dictionary members.");
            }
            _pos++; // consume ','
            SkipOws();
            if (Eof)
            {
                return Fail("Trailing comma in dictionary.");
            }
        }
        members = list.ToArray();
        return true;
    }

    // ------------------------------------------------------------------
    // Item (RFC 9651 §4.2.3)
    // ------------------------------------------------------------------

    private bool ParseItem(out StructuredFieldItem result)
    {
        result = default;
        if (!ParseBareItem(out StructuredFieldBareItem bareItem))
        {
            return false;
        }
        if (!ParseParameters(out StructuredFieldParameters parameters))
        {
            return false;
        }
        result = new StructuredFieldItem(bareItem, parameters);
        return true;
    }

    // ------------------------------------------------------------------
    // Parameters (RFC 9651 §4.2.3.2)
    // ------------------------------------------------------------------

    private bool ParseParameters(out StructuredFieldParameters result)
    {
        result = StructuredFieldParameters.Empty;
        List<KeyValuePair<string, StructuredFieldBareItem>>? list = null;
        while (!Eof && Current == ';')
        {
            _pos++; // consume ';'
            SkipSp();
            if (!ParseKey(out string key))
            {
                return false;
            }
            StructuredFieldBareItem value = StructuredFieldBareItem.CreateBoolean(true);
            if (!Eof && Current == '=')
            {
                _pos++; // consume '='
                if (!ParseBareItem(out value))
                {
                    return false;
                }
            }
            list ??= new List<KeyValuePair<string, StructuredFieldBareItem>>();
            StructuredFieldParameters.Upsert(list, key, value);
        }
        if (list is not null)
        {
            result = StructuredFieldParameters.CreateRaw(list.ToArray());
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Key (RFC 9651 §4.2.3.3)
    // ------------------------------------------------------------------

    private bool ParseKey(out string key)
    {
        key = string.Empty;
        if (Eof || !StructuredFieldGrammar.IsKeyStartChar(Current))
        {
            return Fail("Expected a key.");
        }
        int start = _pos;
        _pos++;
        while (!Eof && StructuredFieldGrammar.IsKeyTailChar(Current))
        {
            _pos++;
        }
        key = _input.Slice(start, _pos - start).ToString();
        return true;
    }

    // ------------------------------------------------------------------
    // Bare item (RFC 9651 §4.2.3.1)
    // ------------------------------------------------------------------

    private bool ParseBareItem(out StructuredFieldBareItem result)
    {
        result = default;
        if (Eof)
        {
            return Fail("Expected a bare item.");
        }
        char c = Current;
        if (c == '-' || StructuredFieldGrammar.IsDigit(c))
        {
            return ParseIntegerOrDecimal(out result);
        }
        if (c == '"')
        {
            return ParseString(out result);
        }
        if (c == ':')
        {
            return ParseByteSequence(out result);
        }
        if (c == '?')
        {
            return ParseBoolean(out result);
        }
        if (c == '@')
        {
            return ParseDate(out result);
        }
        if (c == '%')
        {
            return ParseDisplayString(out result);
        }
        if (StructuredFieldGrammar.IsAlpha(c) || c == '*')
        {
            return ParseToken(out result);
        }
        return Fail($"Unexpected character '{c}' at start of a bare item.");
    }

    // ------------------------------------------------------------------
    // Integer or Decimal (RFC 9651 §4.2.4)
    // ------------------------------------------------------------------

    private bool ParseIntegerOrDecimal(out StructuredFieldBareItem result)
    {
        result = default;
        if (Eof)
        {
            return Fail("Expected a number.");
        }
        int start = _pos;
        if (Current == '-')
        {
            _pos++;
        }
        if (Eof)
        {
            return Fail("Expected a digit after '-'.");
        }
        if (!StructuredFieldGrammar.IsDigit(Current))
        {
            return Fail("Expected a digit.");
        }

        bool isDecimal = false;
        int numberLength = 0; // digits and the '.', excluding the sign
        int integerDigits = 0;
        int fractionDigits = 0;
        bool sawDot = false;

        while (!Eof)
        {
            char c = Current;
            if (StructuredFieldGrammar.IsDigit(c))
            {
                _pos++;
                numberLength++;
                if (sawDot)
                {
                    fractionDigits++;
                }
                else
                {
                    integerDigits++;
                }
            }
            else if (!isDecimal && c == '.')
            {
                if (integerDigits > 12)
                {
                    return Fail("Decimal has more than 12 integer digits.");
                }
                _pos++;
                numberLength++;
                isDecimal = true;
                sawDot = true;
            }
            else
            {
                break;
            }

            if (!isDecimal && numberLength > 15)
            {
                return Fail("Integer has more than 15 digits.");
            }
            if (isDecimal && numberLength > 16)
            {
                return Fail("Decimal is too long.");
            }
        }

        ReadOnlySpan<char> numberSpan = _input.Slice(start, _pos - start);
        if (!isDecimal)
        {
            long integerValue = long.Parse(numberSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            result = StructuredFieldBareItem.CreateInteger(integerValue);
            return true;
        }

        if (fractionDigits == 0)
        {
            return Fail("Decimal has no digits after the decimal point.");
        }
        if (fractionDigits > 3)
        {
            return Fail("Decimal has more than three fractional digits.");
        }
        decimal decimalValue = decimal.Parse(
            numberSpan,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);
        result = StructuredFieldBareItem.CreateDecimal(decimalValue);
        return true;
    }

    // ------------------------------------------------------------------
    // String (RFC 9651 §4.2.5)
    // ------------------------------------------------------------------

    private bool ParseString(out StructuredFieldBareItem result)
    {
        result = default;
        _pos++; // consume opening DQUOTE
        int start = _pos;

        // Fast path: scan for a closing quote with no escapes and slice directly.
        while (!Eof)
        {
            char c = Current;
            if (c == '\\')
            {
                break;
            }
            if (c == '"')
            {
                string value = _input.Slice(start, _pos - start).ToString();
                _pos++; // consume closing DQUOTE
                result = StructuredFieldBareItem.CreateString(value);
                return true;
            }
            if (c < ' ' || c > '~')
            {
                return Fail("String contains a character outside the printable ASCII range.");
            }
            _pos++;
        }

        if (Eof)
        {
            return Fail("Unterminated string.");
        }

        // Slow path: at least one escape sequence is present.
        var builder = new StringBuilder();
        builder.Append(_input.Slice(start, _pos - start));
        while (!Eof)
        {
            char c = _input[_pos++];
            if (c == '\\')
            {
                if (Eof)
                {
                    return Fail("String ends with a trailing backslash.");
                }
                char next = _input[_pos++];
                if (next != '"' && next != '\\')
                {
                    return Fail("Invalid escape sequence in string.");
                }
                builder.Append(next);
            }
            else if (c == '"')
            {
                result = StructuredFieldBareItem.CreateString(builder.ToString());
                return true;
            }
            else if (c < ' ' || c > '~')
            {
                return Fail("String contains a character outside the printable ASCII range.");
            }
            else
            {
                builder.Append(c);
            }
        }
        return Fail("Unterminated string.");
    }

    // ------------------------------------------------------------------
    // Token (RFC 9651 §4.2.6)
    // ------------------------------------------------------------------

    private bool ParseToken(out StructuredFieldBareItem result)
    {
        int start = _pos;
        _pos++; // consume the leading ALPHA / '*'
        while (!Eof && StructuredFieldGrammar.IsTokenTailChar(Current))
        {
            _pos++;
        }
        result = StructuredFieldBareItem.CreateToken(_input.Slice(start, _pos - start).ToString());
        return true;
    }

    // ------------------------------------------------------------------
    // Byte Sequence (RFC 9651 §4.2.7)
    // ------------------------------------------------------------------

    private bool ParseByteSequence(out StructuredFieldBareItem result)
    {
        result = default;
        _pos++; // consume opening ':'
        int start = _pos;
        int close = -1;
        for (int i = _pos; i < _input.Length; i++)
        {
            if (_input[i] == ':')
            {
                close = i;
                break;
            }
        }
        if (close < 0)
        {
            return Fail("Unterminated byte sequence.");
        }

        ReadOnlySpan<char> content = _input.Slice(start, close - start);
        foreach (char c in content)
        {
            if (!StructuredFieldGrammar.IsAlpha(c) && !StructuredFieldGrammar.IsDigit(c)
                && c != '+' && c != '/' && c != '=')
            {
                return Fail("Byte sequence contains a character outside the base64 alphabet.");
            }
        }

        byte[] buffer = new byte[((content.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64Chars(content, buffer, out int written))
        {
            return Fail("Byte sequence is not valid base64.");
        }

        _pos = close + 1; // consume the content and closing ':'
        byte[] bytes = written == buffer.Length ? buffer : buffer[..written];
        result = StructuredFieldBareItem.CreateByteSequence(bytes);
        return true;
    }

    // ------------------------------------------------------------------
    // Boolean (RFC 9651 §4.2.8)
    // ------------------------------------------------------------------

    private bool ParseBoolean(out StructuredFieldBareItem result)
    {
        result = default;
        _pos++; // consume '?'
        if (Eof)
        {
            return Fail("Expected '0' or '1' after '?'.");
        }
        char c = Current;
        if (c == '1')
        {
            _pos++;
            result = StructuredFieldBareItem.CreateBoolean(true);
            return true;
        }
        if (c == '0')
        {
            _pos++;
            result = StructuredFieldBareItem.CreateBoolean(false);
            return true;
        }
        return Fail("Boolean must be '?0' or '?1'.");
    }

    // ------------------------------------------------------------------
    // Date (RFC 9651 §4.2.9)
    // ------------------------------------------------------------------

    private bool ParseDate(out StructuredFieldBareItem result)
    {
        result = default;
        _pos++; // consume '@'
        if (!ParseIntegerOrDecimal(out StructuredFieldBareItem number))
        {
            return false;
        }
        if (number.Type != StructuredFieldType.Integer)
        {
            return Fail("Date must be an integer number of seconds.");
        }
        result = StructuredFieldBareItem.CreateDate(number.AsInteger());
        return true;
    }

    // ------------------------------------------------------------------
    // Display String (RFC 9651 §4.2.10)
    // ------------------------------------------------------------------

    private bool ParseDisplayString(out StructuredFieldBareItem result)
    {
        result = default;
        _pos++; // consume '%'
        if (Eof || Current != '"')
        {
            return Fail("Display string must begin with '%\"'.");
        }
        _pos++; // consume DQUOTE

        var bytes = new List<byte>();
        while (!Eof)
        {
            char c = _input[_pos++];
            if (c == '%')
            {
                if (_pos + 2 > _input.Length)
                {
                    return Fail("Truncated percent-encoding in display string.");
                }
                char h1 = _input[_pos];
                char h2 = _input[_pos + 1];
                if (!StructuredFieldGrammar.IsLowerHex(h1) || !StructuredFieldGrammar.IsLowerHex(h2))
                {
                    return Fail("Display string percent-encoding must use two lowercase hex digits.");
                }
                _pos += 2;
                bytes.Add((byte)((HexValue(h1) << 4) | HexValue(h2)));
            }
            else if (c == '"')
            {
                string value;
                try
                {
                    value = StructuredFieldGrammar.StrictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    return Fail("Display string is not valid UTF-8.");
                }
                result = StructuredFieldBareItem.CreateDisplayString(value);
                return true;
            }
            else if (c < ' ' || c > '~')
            {
                return Fail("Display string contains a character outside the printable ASCII range.");
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
        return Fail("Unterminated display string.");
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
}
