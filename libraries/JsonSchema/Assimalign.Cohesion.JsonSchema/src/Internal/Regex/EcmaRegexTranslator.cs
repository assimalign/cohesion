using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Translates an ECMA-262 regular expression, read with Unicode (<c>u</c> flag) semantics, into an
/// equivalent .NET pattern, rejecting syntax ECMA-262 does not define.
/// </summary>
/// <remarks>
/// <para>
/// JSON Schema patterns are ECMA-262 regular expressions (2020-12 Core §6.4). .NET differs in ways
/// that change results, so every construct whose meaning differs is rewritten: <c>\d</c>, <c>\w</c>,
/// <c>\s</c> and <c>\b</c> use ECMA-262's ASCII and whitespace sets, <c>$</c> matches only at the end
/// of input, <c>.</c> excludes ECMA-262's four line terminators, and astral code points (written
/// literally, as surrogate escapes or as <c>\u{...}</c>) match as one unit so quantifiers apply to
/// the whole code point. Capturing groups are renamed by their ECMA-262 position, because .NET
/// numbers named groups after unnamed ones, and backreferences to groups that did not participate
/// match the empty string as ECMA-262 requires.
/// </para>
/// <para>
/// Syntax only .NET understands is an error: inline options <c>(?i)</c>, comments <c>(?#...)</c>,
/// atomic and conditional groups, Python's <c>(?P...)</c>, and letter escapes ECMA-262 does not define
/// (<c>\a</c>, <c>\A</c>, <c>\z</c>, <c>\Z</c>, <c>\G</c>, <c>\e</c>, ...). Following Annex B, an
/// escaped ASCII punctuation character and a brace that does not start a quantifier are literals.
/// </para>
/// </remarks>
[DebuggerDisplay("{Pattern,nq} @ {Position}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EcmaRegexTranslator
{
    private const string anyCodePoint = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[\s\S])";
    private const string anyExceptLineTerminator = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\n\r\u2028\u2029])";
    private const string wordClass = "a-zA-Z0-9_";
    private const string wordBoundary = "(?:(?<=[" + wordClass + "])(?![" + wordClass + "])|(?<![" + wordClass + "])(?=[" + wordClass + "]))";
    private const string nonWordBoundary = "(?:(?<=[" + wordClass + "])(?=[" + wordClass + "])|(?<![" + wordClass + "])(?![" + wordClass + "]))";

    private static readonly CodePointRange[] _digitRanges = [new('0', '9')];
    private static readonly CodePointRange[] _wordRanges = [new('0', '9'), new('A', 'Z'), new('_', '_'), new('a', 'z')];
    private static readonly CodePointRange[] _spaceRanges =
    [
        new(0x09, 0x0D), new(0x20, 0x20), new(0xA0, 0xA0), new(0x1680, 0x1680), new(0x2000, 0x200A),
        new(0x2028, 0x2029), new(0x202F, 0x202F), new(0x205F, 0x205F), new(0x3000, 0x3000), new(0xFEFF, 0xFEFF),
    ];

    private static readonly Dictionary<string, string> _generalCategories = CreateGeneralCategories();

    private readonly string _pattern;
    private readonly StringBuilder _output;
    private readonly Dictionary<string, int> _groupNames = new(StringComparer.Ordinal);
    private int _position;
    private int _groupCount;
    private int _totalGroups;

    private EcmaRegexTranslator(string pattern)
    {
        _pattern = pattern;
        _output = new StringBuilder(pattern.Length + 16);
    }

    /// <summary>Gets the ECMA-262 pattern being translated.</summary>
    public string Pattern => _pattern;

    /// <summary>Gets the position in <see cref="Pattern"/> of the next character to read.</summary>
    public int Position => _position;

    /// <summary>
    /// Translates a pattern, or reports why it is not a supported ECMA-262 regular expression.
    /// </summary>
    public static bool TryTranslate(string pattern, out string translated, out string? error)
    {
        EcmaRegexTranslator translator = new(pattern);
        try
        {
            translator._totalGroups = translator.CountGroups();
            translator.ParseDisjunction();
            if (translator._position != pattern.Length)
            {
                throw new FormatException(ErrorMessages.RegexUnmatchedParenthesis);
            }

            translated = translator._output.ToString();
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            translated = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    private bool AtEnd => _position >= _pattern.Length;

    private char Current => _pattern[_position];

    private void ParseDisjunction()
    {
        ParseAlternative();
        while (!AtEnd && Current == '|')
        {
            _position++;
            _output.Append('|');
            ParseAlternative();
        }
    }

    private void ParseAlternative()
    {
        while (!AtEnd && Current != '|' && Current != ')')
        {
            ParseTerm();
        }
    }

    private void ParseTerm()
    {
        char c = Current;
        bool quantifiable;
        switch (c)
        {
            case '^':
                _position++;
                _output.Append('^');
                quantifiable = false;
                break;
            case '$':
                _position++;
                _output.Append(@"\z");
                quantifiable = false;
                break;
            case '(':
                quantifiable = ParseGroup();
                break;
            case '.':
                _position++;
                _output.Append(anyExceptLineTerminator);
                quantifiable = true;
                break;
            case '[':
                ParseClass();
                quantifiable = true;
                break;
            case '\\':
                quantifiable = ParseAtomEscape();
                break;
            case '*':
            case '+':
            case '?':
                throw new FormatException(ErrorMessages.RegexNothingToRepeat);
            case '{':
                if (TryReadQuantifierBraces(out _, out _, out _))
                {
                    throw new FormatException(ErrorMessages.RegexNothingToRepeat);
                }

                _position++;
                _output.Append(@"\{");
                quantifiable = true;
                break;
            default:
                AppendLiteralCodePoint(ReadCodePoint());
                quantifiable = true;
                break;
        }

        if (!AtEnd)
        {
            ParseQuantifier(quantifiable);
        }
    }

    private void ParseQuantifier(bool quantifiable)
    {
        char c = Current;
        if (c == '*' || c == '+' || c == '?')
        {
            if (!quantifiable)
            {
                throw new FormatException(ErrorMessages.RegexNothingToRepeat);
            }

            _position++;
            _output.Append(c);
        }
        else if (c == '{' && TryReadQuantifierBraces(out int min, out int max, out int length))
        {
            if (!quantifiable)
            {
                throw new FormatException(ErrorMessages.RegexNothingToRepeat);
            }

            if (max >= 0 && max < min)
            {
                throw new FormatException(ErrorMessages.RegexQuantifierRangeInvalid);
            }

            _output.Append(_pattern, _position, length);
            _position += length;
        }
        else
        {
            return;
        }

        if (!AtEnd && Current == '?')
        {
            _position++;
            _output.Append('?');
        }

        if (!AtEnd && (Current == '*' || Current == '+' || Current == '?' || (Current == '{' && TryReadQuantifierBraces(out _, out _, out _))))
        {
            throw new FormatException(ErrorMessages.RegexNothingToRepeat);
        }
    }

    // {n}, {n,} or {n,m}; max is -1 when unbounded.
    private bool TryReadQuantifierBraces(out int min, out int max, out int length)
    {
        min = 0;
        max = -1;
        length = 0;
        int i = _position + 1;
        int start = i;
        while (i < _pattern.Length && char.IsAsciiDigit(_pattern[i]))
        {
            i++;
        }

        if (i == start || !int.TryParse(_pattern.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out min))
        {
            return false;
        }

        if (i < _pattern.Length && _pattern[i] == '}')
        {
            max = min;
            length = i + 1 - _position;
            return true;
        }

        if (i >= _pattern.Length || _pattern[i] != ',')
        {
            return false;
        }

        i++;
        int maxStart = i;
        while (i < _pattern.Length && char.IsAsciiDigit(_pattern[i]))
        {
            i++;
        }

        if (i >= _pattern.Length || _pattern[i] != '}')
        {
            return false;
        }

        if (i > maxStart && !int.TryParse(_pattern.AsSpan(maxStart, i - maxStart), NumberStyles.None, CultureInfo.InvariantCulture, out max))
        {
            return false;
        }

        length = i + 1 - _position;
        return true;
    }

    // Returns whether the group may be quantified.
    private bool ParseGroup()
    {
        _position++;
        bool quantifiable = true;
        if (!AtEnd && Current == '?')
        {
            _position++;
            if (AtEnd)
            {
                throw new FormatException(ErrorMessages.RegexGroupInvalid);
            }

            char kind = Current;
            if (kind == ':' || kind == '=' || kind == '!')
            {
                _position++;
                _output.Append("(?").Append(kind);
            }
            else if (kind == '<' && _position + 1 < _pattern.Length && (_pattern[_position + 1] == '=' || _pattern[_position + 1] == '!'))
            {
                _output.Append("(?<").Append(_pattern[_position + 1]);
                _position += 2;
                quantifiable = false;
            }
            else if (kind == '<')
            {
                _position++;
                string name = ReadGroupName();
                if (!_groupNames.TryAdd(name, ++_groupCount))
                {
                    throw new FormatException(ErrorMessages.RegexGroupNameDuplicate);
                }

                _output.Append("(?<_").Append(_groupCount.ToString(CultureInfo.InvariantCulture)).Append('>');
            }
            else
            {
                throw new FormatException(ErrorMessages.RegexGroupInvalid);
            }
        }
        else
        {
            _groupCount++;
            _output.Append("(?<_").Append(_groupCount.ToString(CultureInfo.InvariantCulture)).Append('>');
        }

        ParseDisjunction();
        if (AtEnd || Current != ')')
        {
            throw new FormatException(ErrorMessages.RegexUnmatchedParenthesis);
        }

        _position++;
        _output.Append(')');
        return quantifiable;
    }

    private string ReadGroupName()
    {
        int start = _position;
        while (!AtEnd && Current != '>')
        {
            char c = Current;
            bool valid = _position == start
                ? char.IsLetter(c) || c == '$' || c == '_'
                : char.IsLetterOrDigit(c) || c == '$' || c == '_';
            if (!valid)
            {
                throw new FormatException(ErrorMessages.RegexGroupNameInvalid);
            }

            _position++;
        }

        if (AtEnd || _position == start)
        {
            throw new FormatException(ErrorMessages.RegexGroupNameInvalid);
        }

        string name = _pattern.Substring(start, _position - start);
        _position++;
        return name;
    }

    // Returns whether the escape is an atom (quantifiable) rather than an assertion.
    private bool ParseAtomEscape()
    {
        _position++;
        if (AtEnd)
        {
            throw new FormatException(ErrorMessages.RegexTrailingBackslash);
        }

        char c = Current;
        switch (c)
        {
            case 'b':
                _position++;
                _output.Append(wordBoundary);
                return false;
            case 'B':
                _position++;
                _output.Append(nonWordBoundary);
                return false;
            case 'd':
                _position++;
                _output.Append("[0-9]");
                return true;
            case 'D':
                _position++;
                AppendClass(Complement(_digitRanges), [], negated: false);
                return true;
            case 'w':
                _position++;
                _output.Append('[').Append(wordClass).Append(']');
                return true;
            case 'W':
                _position++;
                AppendClass(Complement(_wordRanges), [], negated: false);
                return true;
            case 's':
                _position++;
                AppendClass(_spaceRanges, [], negated: false);
                return true;
            case 'S':
                _position++;
                AppendClass(Complement(_spaceRanges), [], negated: false);
                return true;
            case 'p':
            case 'P':
                _position++;
                _output.Append(ReadProperty(c == 'P'));
                return true;
            case 'k':
                _position++;
                AppendBackreference(ReadNamedReference());
                return true;
            default:
                if (c >= '1' && c <= '9')
                {
                    int start = _position;
                    while (!AtEnd && char.IsAsciiDigit(Current))
                    {
                        _position++;
                    }

                    int group = int.Parse(_pattern.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture);
                    if (group > _totalGroups)
                    {
                        throw new FormatException(ErrorMessages.RegexBackreferenceInvalid);
                    }

                    AppendBackreference(group);
                    return true;
                }

                AppendLiteralCodePoint(ReadCharacterEscape(inClass: false));
                return true;
        }
    }

    private int ReadNamedReference()
    {
        if (AtEnd || Current != '<')
        {
            throw new FormatException(ErrorMessages.RegexBackreferenceInvalid);
        }

        _position++;
        int start = _position;
        while (!AtEnd && Current != '>')
        {
            _position++;
        }

        if (AtEnd)
        {
            throw new FormatException(ErrorMessages.RegexBackreferenceInvalid);
        }

        string name = _pattern.Substring(start, _position - start);
        _position++;
        int group = FindGroupNumber(name);
        if (group <= 0)
        {
            throw new FormatException(ErrorMessages.RegexBackreferenceInvalid);
        }

        return group;
    }

    private void AppendBackreference(int group)
    {
        // ECMA-262: a reference to a group that has not participated matches the empty string.
        string name = "_" + group.ToString(CultureInfo.InvariantCulture);
        _output.Append("(?(").Append(name).Append(@")\k<").Append(name).Append(">|)");
    }

    // A character escape outside the class escapes: control, hex, Unicode and identity escapes.
    private int ReadCharacterEscape(bool inClass)
    {
        char c = Current;
        _position++;
        switch (c)
        {
            case 'f':
                return '\f';
            case 'n':
                return '\n';
            case 'r':
                return '\r';
            case 't':
                return '\t';
            case 'v':
                return '\v';
            case 'c':
                if (AtEnd || !char.IsAsciiLetter(Current))
                {
                    throw new FormatException(ErrorMessages.RegexEscapeInvalid);
                }

                return _pattern[_position++] % 32;
            case '0':
                if (!AtEnd && char.IsAsciiDigit(Current))
                {
                    throw new FormatException(ErrorMessages.RegexEscapeInvalid);
                }

                return 0;
            case 'x':
                return ReadHex(2);
            case 'u':
                return ReadUnicodeEscape();
            case '-' when inClass:
                return '-';
            default:
                if (c < 0x80 && char.IsAsciiLetterOrDigit(c))
                {
                    throw new FormatException(ErrorMessages.RegexEscapeInvalid);
                }

                if (char.IsHighSurrogate(c) && !AtEnd && char.IsLowSurrogate(Current))
                {
                    return char.ConvertToUtf32(c, _pattern[_position++]);
                }

                return c;
        }
    }

    private int ReadUnicodeEscape()
    {
        if (!AtEnd && Current == '{')
        {
            _position++;
            int start = _position;
            int value = 0;
            while (!AtEnd && char.IsAsciiHexDigit(Current))
            {
                value = (value * 16) + HexValue(Current);
                if (value > 0x10FFFF)
                {
                    throw new FormatException(ErrorMessages.RegexEscapeInvalid);
                }

                _position++;
            }

            if (_position == start || AtEnd || Current != '}')
            {
                throw new FormatException(ErrorMessages.RegexEscapeInvalid);
            }

            _position++;
            return value;
        }

        int unit = ReadHex(4);
        if (unit >= 0xD800 && unit <= 0xDBFF
            && _position + 5 < _pattern.Length
            && _pattern[_position] == '\\' && _pattern[_position + 1] == 'u')
        {
            int save = _position;
            _position += 2;
            if (TryReadHex(4, out int low) && low >= 0xDC00 && low <= 0xDFFF)
            {
                return char.ConvertToUtf32((char)unit, (char)low);
            }

            _position = save;
        }

        return unit;
    }

    private int ReadHex(int digits)
    {
        if (!TryReadHex(digits, out int value))
        {
            throw new FormatException(ErrorMessages.RegexEscapeInvalid);
        }

        return value;
    }

    private bool TryReadHex(int digits, out int value)
    {
        value = 0;
        if (_position + digits > _pattern.Length)
        {
            return false;
        }

        for (int i = 0; i < digits; i++)
        {
            char c = _pattern[_position + i];
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            value = (value * 16) + HexValue(c);
        }

        _position += digits;
        return true;
    }

    private string ReadProperty(bool negated)
    {
        if (AtEnd || Current != '{')
        {
            throw new FormatException(ErrorMessages.RegexPropertyInvalid);
        }

        int close = _pattern.IndexOf('}', _position);
        if (close < 0)
        {
            throw new FormatException(ErrorMessages.RegexPropertyInvalid);
        }

        string name = _pattern.Substring(_position + 1, close - _position - 1);
        _position = close + 1;
        int equals = name.IndexOf('=');
        if (equals >= 0)
        {
            string key = name.Substring(0, equals);
            if (key is not ("General_Category" or "gc"))
            {
                // Script and Script_Extensions need tables .NET does not expose.
                throw new FormatException(ErrorMessages.RegexPropertyUnsupported);
            }

            name = name.Substring(equals + 1);
        }

        return name switch
        {
            "Any" => negated ? "(?!)" : anyCodePoint,
            "ASCII" => negated ? @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\x00-\x7F])" : @"[\x00-\x7F]",
            "Assigned" => negated ? @"\p{Cn}" : @"\P{Cn}",
            _ => _generalCategories.TryGetValue(name, out string? category)
                ? FormatCategory(category, negated)
                : throw new FormatException(ErrorMessages.RegexPropertyUnsupported),
        };
    }

    private static string FormatCategory(string category, bool negated)
    {
        if (category == "LC")
        {
            return negated ? @"[^\p{Lu}\p{Ll}\p{Lt}]" : @"[\p{Lu}\p{Ll}\p{Lt}]";
        }

        return (negated ? @"\P{" : @"\p{") + category + "}";
    }

    private void ParseClass()
    {
        _position++;
        bool negated = false;
        if (!AtEnd && Current == '^')
        {
            negated = true;
            _position++;
        }

        List<CodePointRange> ranges = new();
        List<string> categories = new();
        while (true)
        {
            if (AtEnd)
            {
                throw new FormatException(ErrorMessages.RegexClassUnterminated);
            }

            if (Current == ']')
            {
                _position++;
                break;
            }

            ClassAtom first = ReadClassAtom();
            if (first.IsSet || AtEnd || Current != '-' || _position + 1 >= _pattern.Length || _pattern[_position + 1] == ']')
            {
                Add(first, ranges, categories);
                continue;
            }

            _position++;
            ClassAtom second = ReadClassAtom();
            if (second.IsSet)
            {
                // Annex B: a class escape at either end makes the '-' a literal.
                Add(first, ranges, categories);
                ranges.Add(new CodePointRange('-', '-'));
                Add(second, ranges, categories);
                continue;
            }

            if (second.CodePoint < first.CodePoint)
            {
                throw new FormatException(ErrorMessages.RegexClassRangeInvalid);
            }

            ranges.Add(new CodePointRange(first.CodePoint, second.CodePoint));
        }

        AppendClass(Normalize(ranges), categories, negated);
    }

    private ClassAtom ReadClassAtom()
    {
        char c = Current;
        if (c != '\\')
        {
            return ClassAtom.Single(ReadCodePoint());
        }

        _position++;
        if (AtEnd)
        {
            throw new FormatException(ErrorMessages.RegexTrailingBackslash);
        }

        char escape = Current;
        switch (escape)
        {
            case 'b':
                _position++;
                return ClassAtom.Single('\b');
            case 'd':
                _position++;
                return ClassAtom.Set(_digitRanges, null);
            case 'D':
                _position++;
                return ClassAtom.Set(Complement(_digitRanges), null);
            case 'w':
                _position++;
                return ClassAtom.Set(_wordRanges, null);
            case 'W':
                _position++;
                return ClassAtom.Set(Complement(_wordRanges), null);
            case 's':
                _position++;
                return ClassAtom.Set(_spaceRanges, null);
            case 'S':
                _position++;
                return ClassAtom.Set(Complement(_spaceRanges), null);
            case 'p':
            case 'P':
                _position++;
                return ClassAtom.Set(null, ReadClassProperty(escape == 'P'));
            default:
                if (escape >= '1' && escape <= '9')
                {
                    throw new FormatException(ErrorMessages.RegexEscapeInvalid);
                }

                return ClassAtom.Single(ReadCharacterEscape(inClass: true));
        }
    }

    private string ReadClassProperty(bool negated)
    {
        string property = ReadProperty(negated);
        if (property.StartsWith(@"\p{", StringComparison.Ordinal) || property.StartsWith(@"\P{", StringComparison.Ordinal))
        {
            return property;
        }

        // LC, ASCII, Any and Assigned expand to constructs a .NET class cannot nest.
        throw new FormatException(ErrorMessages.RegexPropertyUnsupported);
    }

    private static void Add(ClassAtom atom, List<CodePointRange> ranges, List<string> categories)
    {
        if (atom.Ranges is not null)
        {
            ranges.AddRange(atom.Ranges);
        }
        else if (atom.Category is not null)
        {
            categories.Add(atom.Category);
        }
        else
        {
            ranges.Add(new CodePointRange(atom.CodePoint, atom.CodePoint));
        }
    }

    private void AppendClass(IReadOnlyList<CodePointRange> ranges, IReadOnlyList<string> categories, bool negated)
    {
        StringBuilder bmp = new();
        List<CodePointRange> astral = new();
        foreach (CodePointRange range in ranges)
        {
            if (range.Start <= 0xFFFF)
            {
                int end = Math.Min(range.End, 0xFFFF);
                AppendClassChar(bmp, range.Start);
                if (end > range.Start)
                {
                    bmp.Append('-');
                    AppendClassChar(bmp, end);
                }
            }

            if (range.End > 0xFFFF)
            {
                astral.Add(new CodePointRange(Math.Max(range.Start, 0x10000), range.End));
            }
        }

        foreach (string category in categories)
        {
            bmp.Append(category);
        }

        if (!negated)
        {
            List<string> alternatives = new();
            if (bmp.Length > 0)
            {
                alternatives.Add("[" + bmp + "]");
            }

            foreach (CodePointRange range in astral)
            {
                alternatives.Add(SurrogateAlternatives(range));
            }

            if (alternatives.Count == 0)
            {
                _output.Append("(?!)");
            }
            else if (alternatives.Count == 1)
            {
                _output.Append(alternatives[0]);
            }
            else
            {
                _output.Append("(?:").Append(string.Join('|', alternatives)).Append(')');
            }

            return;
        }

        if (astral.Count == 0)
        {
            _output.Append(bmp.Length == 0 ? anyCodePoint : @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^" + bmp + "])");
            return;
        }

        // A negated class with astral members: any code point not matched by the positive class.
        int save = _output.Length;
        AppendClass(ranges, categories, negated: false);
        string positive = _output.ToString(save, _output.Length - save);
        _output.Length = save;
        _output.Append("(?:(?!").Append(positive).Append(')').Append(anyCodePoint).Append(')');
    }

    private void AppendLiteralCodePoint(int codePoint)
    {
        if (codePoint > 0xFFFF)
        {
            string pair = char.ConvertFromUtf32(codePoint);
            _output.Append("(?:");
            AppendEscapedUnit(_output, pair[0]);
            AppendEscapedUnit(_output, pair[1]);
            _output.Append(')');
            return;
        }

        char c = (char)codePoint;
        if (char.IsAsciiLetterOrDigit(c) || c == '_' || (c > 0x7F && !char.IsSurrogate(c) && !char.IsWhiteSpace(c)))
        {
            _output.Append(c);
        }
        else
        {
            AppendEscapedUnit(_output, c);
        }
    }

    private int ReadCodePoint()
    {
        char c = _pattern[_position++];
        if (char.IsHighSurrogate(c) && !AtEnd && char.IsLowSurrogate(Current))
        {
            return char.ConvertToUtf32(c, _pattern[_position++]);
        }

        return c;
    }

    private int FindGroupNumber(string name) => _groupNames.TryGetValue(name, out int number) ? number : FindForwardGroup(name);

    // A named backreference may precede its group; find the group's ECMA position by scanning.
    private int FindForwardGroup(string name)
    {
        int count = 0;
        for (int i = 0; i < _pattern.Length; i++)
        {
            char c = _pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                i = SkipClass(i);
                continue;
            }

            if (c != '(')
            {
                continue;
            }

            if (i + 1 < _pattern.Length && _pattern[i + 1] == '?')
            {
                if (i + 2 < _pattern.Length && _pattern[i + 2] == '<' && i + 3 < _pattern.Length && _pattern[i + 3] != '=' && _pattern[i + 3] != '!')
                {
                    count++;
                    int close = _pattern.IndexOf('>', i + 3);
                    if (close > 0 && string.CompareOrdinal(_pattern, i + 3, name, 0, Math.Max(name.Length, close - i - 3)) == 0 && close - i - 3 == name.Length)
                    {
                        return count;
                    }
                }

                continue;
            }

            count++;
        }

        return -1;
    }

    private int CountGroups()
    {
        int count = 0;
        for (int i = 0; i < _pattern.Length; i++)
        {
            char c = _pattern[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '[')
            {
                i = SkipClass(i);
            }
            else if (c == '(')
            {
                if (i + 1 < _pattern.Length && _pattern[i + 1] == '?')
                {
                    if (i + 3 < _pattern.Length && _pattern[i + 2] == '<' && _pattern[i + 3] != '=' && _pattern[i + 3] != '!')
                    {
                        count++;
                    }
                }
                else
                {
                    count++;
                }
            }
        }

        return count;
    }

    private int SkipClass(int open)
    {
        int i = open + 1;
        if (i < _pattern.Length && _pattern[i] == '^')
        {
            i++;
        }

        for (; i < _pattern.Length; i++)
        {
            if (_pattern[i] == '\\')
            {
                i++;
            }
            else if (_pattern[i] == ']')
            {
                return i;
            }
        }

        return _pattern.Length;
    }

    private static string SurrogateAlternatives(CodePointRange range)
    {
        string start = char.ConvertFromUtf32(range.Start);
        string end = char.ConvertFromUtf32(range.End);
        char startHigh = start[0];
        char startLow = start[1];
        char endHigh = end[0];
        char endLow = end[1];
        StringBuilder builder = new();
        if (startHigh == endHigh)
        {
            AppendEscapedUnit(builder, startHigh);
            AppendUnitRange(builder, startLow, endLow);
            return builder.ToString();
        }

        builder.Append("(?:");
        AppendEscapedUnit(builder, startHigh);
        AppendUnitRange(builder, startLow, '\uDFFF');
        if (endHigh - startHigh > 1)
        {
            builder.Append('|');
            AppendUnitRange(builder, (char)(startHigh + 1), (char)(endHigh - 1));
            builder.Append(@"[\uDC00-\uDFFF]");
        }

        builder.Append('|');
        AppendEscapedUnit(builder, endHigh);
        AppendUnitRange(builder, '\uDC00', endLow);
        builder.Append(')');
        return builder.ToString();
    }

    private static void AppendUnitRange(StringBuilder builder, char start, char end)
    {
        builder.Append('[');
        AppendEscapedUnit(builder, start);
        if (end > start)
        {
            builder.Append('-');
            AppendEscapedUnit(builder, end);
        }

        builder.Append(']');
    }

    private static void AppendClassChar(StringBuilder builder, int codePoint)
    {
        char c = (char)codePoint;
        if (char.IsAsciiLetterOrDigit(c))
        {
            builder.Append(c);
        }
        else
        {
            AppendEscapedUnit(builder, c);
        }
    }

    private static void AppendEscapedUnit(StringBuilder builder, char c)
    {
        builder.Append(@"\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
    }

    private static CodePointRange[] Complement(IReadOnlyList<CodePointRange> ranges)
    {
        List<CodePointRange> result = new();
        int next = 0;
        foreach (CodePointRange range in Normalize(ranges))
        {
            if (range.Start > next)
            {
                result.Add(new CodePointRange(next, range.Start - 1));
            }

            next = range.End + 1;
        }

        if (next <= 0x10FFFF)
        {
            result.Add(new CodePointRange(next, 0x10FFFF));
        }

        return result.ToArray();
    }

    private static List<CodePointRange> Normalize(IEnumerable<CodePointRange> ranges)
    {
        List<CodePointRange> sorted = new(ranges);
        sorted.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        List<CodePointRange> merged = new(sorted.Count);
        foreach (CodePointRange range in sorted)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                CodePointRange last = merged[^1];
                merged[^1] = new CodePointRange(last.Start, Math.Max(last.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    private static Dictionary<string, string> CreateGeneralCategories()
    {
        Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);
        void Add(string shortName, params string[] longNames)
        {
            categories[shortName] = shortName;
            foreach (string longName in longNames)
            {
                categories[longName] = shortName;
            }
        }

        Add("L", "Letter");
        Add("LC", "Cased_Letter");
        Add("Lu", "Uppercase_Letter");
        Add("Ll", "Lowercase_Letter");
        Add("Lt", "Titlecase_Letter");
        Add("Lm", "Modifier_Letter");
        Add("Lo", "Other_Letter");
        Add("M", "Mark", "Combining_Mark");
        Add("Mn", "Nonspacing_Mark");
        Add("Mc", "Spacing_Mark");
        Add("Me", "Enclosing_Mark");
        Add("N", "Number");
        Add("Nd", "Decimal_Number", "digit");
        Add("Nl", "Letter_Number");
        Add("No", "Other_Number");
        Add("P", "Punctuation", "punct");
        Add("Pc", "Connector_Punctuation");
        Add("Pd", "Dash_Punctuation");
        Add("Ps", "Open_Punctuation");
        Add("Pe", "Close_Punctuation");
        Add("Pi", "Initial_Punctuation");
        Add("Pf", "Final_Punctuation");
        Add("Po", "Other_Punctuation");
        Add("S", "Symbol");
        Add("Sm", "Math_Symbol");
        Add("Sc", "Currency_Symbol");
        Add("Sk", "Modifier_Symbol");
        Add("So", "Other_Symbol");
        Add("Z", "Separator");
        Add("Zs", "Space_Separator");
        Add("Zl", "Line_Separator");
        Add("Zp", "Paragraph_Separator");
        Add("C", "Other");
        Add("Cc", "Control", "cntrl");
        Add("Cf", "Format");
        Add("Cs", "Surrogate");
        Add("Co", "Private_Use");
        Add("Cn", "Unassigned");
        return categories;
    }

    private readonly record struct CodePointRange(int Start, int End);

    private readonly struct ClassAtom
    {
        private ClassAtom(int codePoint, CodePointRange[]? ranges, string? category)
        {
            CodePoint = codePoint;
            Ranges = ranges;
            Category = category;
        }

        public int CodePoint { get; }

        public CodePointRange[]? Ranges { get; }

        public string? Category { get; }

        public bool IsSet => Ranges is not null || Category is not null;

        public static ClassAtom Single(int codePoint) => new(codePoint, null, null);

        public static ClassAtom Set(CodePointRange[]? ranges, string? category) => new(-1, ranges, category);
    }

    private sealed class DebugView
    {
        private readonly EcmaRegexTranslator _translator;

        public DebugView(EcmaRegexTranslator translator)
        {
            _translator = translator;
        }

        public string Pattern => _translator.Pattern;

        public int Position => _translator.Position;
    }
}
