using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// Parses route templates (for example <c>/users/{id:int}/files/{**path}</c>) into immutable
/// <see cref="RoutePattern"/> instances.
/// </summary>
public sealed class RoutePatternParser
{

    private const char Separator = '/';
    private const char OpenBrace = '{';
    private const char CloseBrace = '}';
    private const char QuestionMark = '?';
    private const string PeriodString = ".";

    internal static readonly SearchValues<char> InvalidParameterNameChars = SearchValues.Create("/{}?*");

    /// <summary>
    /// Parses a route template into a <see cref="RoutePattern"/>.
    /// </summary>
    /// <param name="pattern">
    /// The route template. A leading <c>/</c> or <c>~/</c> is ignored.
    /// </param>
    /// <returns>The parsed route pattern.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="RoutePatternException">
    /// <paramref name="pattern"/> is not a valid route template. The message names the template and
    /// the part of it that is invalid, and says how to fix it.
    /// </exception>
    public static RoutePattern Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var trimmedPattern = TrimPrefix(pattern);

        var context = new Context(trimmedPattern);
        var segments = new List<RoutePatternPathSegment>();

        while (context.MoveNext())
        {
            var i = context.Index;

            if (context.Current == Separator)
            {
                // If we get here is means that there's a consecutive '/' character.
                // Templates don't start with a '/' and parsing a segment consumes the separator.
                throw CreateException(pattern, ErrorMessages.RouteSegmentEmpty);
            }

            if (!ParseSegment(context, segments))
            {
                throw CreateException(pattern, context.Error);
            }

            // A successful parse should always result in us being at the end or at a separator.
            Debug.Assert(context.AtEnd() || context.Current == Separator);

            if (context.Index <= i)
            {
                // This shouldn't happen, but we want to crash if it does.
                var message = ErrorMessages.RouteParserInfiniteLoop;
                throw new InvalidProgramException(message);
            }
        }

        if (IsAllValid(context, segments))
        {
            return RoutePatternFactory.Pattern(pattern, segments);
        }
        else
        {
            throw CreateException(pattern, context.Error);
        }
    }

    private static RoutePatternException CreateException(string pattern, string? error)
    {
        // Every error path records a specific reason; the fallback only guards a future path that
        // forgets to, so the message is never empty.
        return new RoutePatternException(
            pattern,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteTemplateInvalid, pattern, error ?? ErrorMessages.RouteTemplateParseFailed));
    }

    private static bool ParseSegment(Context context, List<RoutePatternPathSegment> pathSegments)
    {
        Debug.Assert(context != null);
        Debug.Assert(pathSegments != null);

        var patternSegments = new List<RoutePatternSegment>();

        while (true)
        {
            var i = context.Index;

            if (context.Current == OpenBrace)
            {
                if (!context.MoveNext())
                {
                    // This is a dangling open-brace, which is not allowed
                    context.Error = ErrorMessages.RouteParameterOpeningBraceUnmatched;
                    return false;
                }

                if (context.Current == OpenBrace)
                {
                    // This is an 'escaped' brace in a literal, like "{{foo"
                    context.Back();
                    if (!ParseLiteral(context, patternSegments))
                    {
                        return false;
                    }
                }
                else
                {
                    // This is a parameter
                    context.Back();
                    if (!ParseParameter(context, patternSegments))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (!ParseLiteral(context, patternSegments))
                {
                    return false;
                }
            }

            if (context.Current == Separator || context.AtEnd())
            {
                // We've reached the end of the segment
                break;
            }

            if (context.Index <= i)
            {
                // This shouldn't happen, but we want to crash if it does.
                var message = ErrorMessages.RouteParserInfiniteLoop;
                throw new InvalidProgramException(message);
            }
        }

        if (IsSegmentValid(context, patternSegments))
        {
            pathSegments.Add(new RoutePatternPathSegment(patternSegments));
            return true;
        }
        else
        {
            return false;
        }
    }

    private static bool ParseParameter(Context context, List<RoutePatternSegment> parts)
    {
        Debug.Assert(context.Current == OpenBrace);
        context.Mark();

        context.MoveNext();

        while (true)
        {
            if (context.Current == OpenBrace)
            {
                // This is an open brace inside of a parameter, it has to be escaped
                if (context.MoveNext())
                {
                    if (context.Current != OpenBrace)
                    {
                        // If we see something like "{p1:regex(^\d{3", we will come here.
                        context.Error =
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterOpeningBraceUnescaped, context.MarkedText());
                        return false;
                    }
                }
                else
                {
                    // This is a dangling open-brace, which is not allowed
                    // Example: "{p1:regex(^\d{"
                    context.Error =
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterTrailingBraceUnclosed, context.MarkedText());
                    return false;
                }
            }
            else if (context.Current == CloseBrace)
            {
                // When we encounter Closed brace here, it either means end of the parameter or it is a closed
                // brace in the parameter, in that case it needs to be escaped.
                // Example: {p1:regex(([}}])\w+}. First pair is escaped one and last marks end of the parameter
                if (!context.MoveNext())
                {
                    // This is the end of the string -and we have a valid parameter
                    break;
                }

                if (context.Current == CloseBrace)
                {
                    // This is an 'escaped' brace in a parameter name
                }
                else
                {
                    // This is the end of the parameter
                    break;
                }
            }

            if (!context.MoveNext())
            {
                // The template ends inside the parameter: its closing brace is missing.
                context.Error =
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterClosingBraceMissing, context.MarkedText());
                return false;
            }
        }

        var text = context.Capture();
        if (text == "{}")
        {
            context.Error = ErrorMessages.RouteParameterEmpty;
            return false;
        }

        var inside = text.Substring(1, text.Length - 2);
        var decoded = inside.Replace("}}", "}").Replace("{{", "{");

        // At this point, we need to parse the raw name for inline constraint,
        // default values and optional parameters.
        var templatePart = RoutePatternParameterParser.ParseRouteParameter(decoded);

        // See #475 - this is here because InlineRouteParameterParser can't return errors
        if (decoded.StartsWith('*') && decoded.EndsWith('?'))
        {
            context.Error =
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionalCatchAllParameterNotSupported, text);
            return false;
        }

        if (templatePart.IsOptional && templatePart.Default != null)
        {
            // Cannot be optional and have a default value.
            // The only way to declare an optional parameter is to have a ? at the end,
            // hence we cannot have both default value and optional parameter within the template.
            // A workaround is to add it as a separate entry in the defaults argument.
            context.Error =
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionalParameterDefaultNotAllowed, text);
            return false;
        }

        var parameterName = templatePart.Name;
        if (IsValidParameterName(context, parameterName, text))
        {
            parts.Add(templatePart);
            return true;
        }
        else
        {
            return false;
        }
    }

    private static bool ParseLiteral(Context context, List<RoutePatternSegment> parts)
    {
        context.Mark();

        while (true)
        {
            if (context.Current == Separator)
            {
                // End of the segment
                break;
            }
            else if (context.Current == OpenBrace)
            {
                if (!context.MoveNext())
                {
                    // This is a dangling open-brace, which is not allowed
                    context.Error = ErrorMessages.RouteParameterOpeningBraceUnmatched;
                    return false;
                }

                if (context.Current == OpenBrace)
                {
                    // This is an 'escaped' brace in a literal, like "{{foo" - keep going.
                }
                else
                {
                    // We've just seen the start of a parameter, so back up.
                    context.Back();
                    break;
                }
            }
            else if (context.Current == CloseBrace)
            {
                if (!context.MoveNext())
                {
                    // This is a dangling close-brace, which is not allowed
                    context.Error = ErrorMessages.RouteClosingBraceUnmatched;
                    return false;
                }

                if (context.Current == CloseBrace)
                {
                    // This is an 'escaped' brace in a literal, like "{{foo" - keep going.
                }
                else
                {
                    // This is an unbalanced close-brace, which is not allowed
                    context.Error =
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.LiteralRouteClosingBraceUnmatched, context.MarkedText(includeCurrent: false));
                    return false;
                }
            }

            if (!context.MoveNext())
            {
                break;
            }
        }

        var encoded = context.Capture();
        var decoded = encoded.Replace("}}", "}").Replace("{{", "{");
        if (IsValidLiteral(context, decoded))
        {
            parts.Add(RoutePatternFactory.LiteralPart(decoded));
            return true;
        }
        else
        {
            return false;
        }
    }

    private static bool IsAllValid(Context context, List<RoutePatternPathSegment> segments)
    {
        // A catch-all parameter must be the last part of the last segment
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            for (var j = 0; j < segment.Segments.Count; j++)
            {
                var part = segment.Segments[j];
                if (part is RoutePatternParameterSegment parameter
                    && parameter.IsCatchAll &&
                    (i != segments.Count - 1 || j != segment.Segments.Count - 1))
                {
                    context.Error =
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatchAllParameterPositionInvalid, parameter.DebuggerToString());
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSegmentValid(Context context, List<RoutePatternSegment> parts)
    {
        // If a segment has multiple parts, then it can't contain a catch all.
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part is RoutePatternParameterSegment parameter && parameter.IsCatchAll && parts.Count > 1)
            {
                context.Error =
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatchAllParameterSegmentInvalid, RoutePatternPathSegment.DebuggerToString(parts), parameter.DebuggerToString());
                return false;
            }
        }

        // if a segment has multiple parts, then only the last one parameter can be optional
        // if it is following a optional separator.
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];

            if (part is RoutePatternParameterSegment parameter && parameter.IsOptional && parts.Count > 1)
            {
                // This optional parameter is the last part in the segment
                if (i == parts.Count - 1)
                {
                    var previousPart = parts[i - 1];

                    if (!previousPart.IsLiteral && !previousPart.IsSeparator)
                    {
                        // The optional parameter is preceded by something that is not a literal or separator,
                        // for example another parameter: '{name}{ext?}'.
                        context.Error =
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionalParameterDelimiterMissing, RoutePatternPathSegment.DebuggerToString(parts), parameter.Name, previousPart.DebuggerToString(), ErrorMessages.OptionalParameterDelimiterInvalid);

                        return false;
                    }
                    else if (previousPart is RoutePatternLiteralSegment literal && literal.Content != PeriodString)
                    {
                        // The optional parameter is preceded by a literal other than period, for example
                        // '{name}-{ext?}'.
                        context.Error =
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionalParameterSeparatorInvalid, RoutePatternPathSegment.DebuggerToString(parts), parameter.Name, literal.Content, ErrorMessages.OptionalParameterDelimiterInvalid);

                        return false;
                    }

                    parts[i - 1] = RoutePatternFactory.SeparatorPart(((RoutePatternLiteralSegment)previousPart).Content);
                }
                else
                {
                    // This optional parameter is not the last one in the segment, for example
                    // '{name?}.{ext}'.
                    context.Error =
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionalParameterPositionInvalid, RoutePatternPathSegment.DebuggerToString(parts), parameter.Name, parts[i + 1].DebuggerToString());

                    return false;
                }
            }
        }

        // A segment cannot contain two consecutive parameters
        var isLastSegmentParameter = false;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.IsParameter && isLastSegmentParameter)
            {
                string previous = parts[i - 1].DebuggerToString();
                string current = part.DebuggerToString();

                context.Error =
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParametersAdjacent, RoutePatternPathSegment.DebuggerToString(parts), previous, current, previous, current);
                return false;
            }

            isLastSegmentParameter = part.IsParameter;
        }

        return true;
    }

    private static bool IsValidParameterName(Context context, string parameterName, string parameterText)
    {
        if (parameterName.Length == 0)
        {
            context.Error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterNameMissing, parameterText);
            return false;
        }

        int invalidIndex = parameterName.AsSpan().IndexOfAny(InvalidParameterNameChars);
        if (invalidIndex >= 0)
        {
            context.Error =
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterNameInvalid, parameterName, parameterName[invalidIndex]);
            return false;
        }

        if (!context.ParameterNames.Add(parameterName))
        {
            context.Error =
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteParameterNameRepeated, parameterName);
            return false;
        }

        return true;
    }

    private static bool IsValidLiteral(Context context, string literal)
    {
        Debug.Assert(context != null);
        Debug.Assert(literal != null);

        if (literal.Contains(QuestionMark))
        {
            context.Error =
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteLiteralQueryMarkerNotAllowed, literal);
            return false;
        }

        return true;
    }

    private static string TrimPrefix(string routePattern)
    {
        if (routePattern.StartsWith("~/", StringComparison.Ordinal))
        {
            return routePattern.Substring(2);
        }
        else if (routePattern.StartsWith('/'))
        {
            return routePattern.Substring(1);
        }
        else if (routePattern.StartsWith('~'))
        {
            throw CreateException(
                routePattern,
                ErrorMessages.RoutePrefixInvalid);
        }
        return routePattern;
    }


    [DebuggerDisplay("{DebuggerToString()}")]
    internal sealed class Context
    {
        private readonly HashSet<string> _parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _template;
        private int _index;
        private int? _mark;


        public Context(string template)
        {
            Debug.Assert(template != null);
            _template = template;

            _index = -1;
        }

        public char Current => (_index < _template.Length && _index >= 0) ? _template[_index] : (char)0;

        public int Index => _index;

        public string? Error
        {
            get;
            set;
        }

        public HashSet<string> ParameterNames => _parameterNames;

        public bool Back()
        {
            return --_index >= 0;
        }

        public bool AtEnd()
        {
            return _index >= _template.Length;
        }

        public bool MoveNext()
        {
            return ++_index < _template.Length;
        }

        public void Mark()
        {
            Debug.Assert(_index >= 0);

            // Index is always the index of the character *past* Current - we want to 'mark' Current.
            _mark = _index;
        }

        public string Capture()
        {
            if (_mark.HasValue)
            {
                var value = _template.Substring(_mark.Value, _index - _mark.Value);
                _mark = null;
                return value;
            }

            return string.Empty;
        }

        // The text from the mark up to the current character (inclusive by default), without clearing
        // the mark: error messages quote the part of the template being parsed when the error is found.
        public string MarkedText(bool includeCurrent = true)
        {
            if (!_mark.HasValue)
            {
                return string.Empty;
            }

            int end = Math.Min(includeCurrent ? _index + 1 : _index, _template.Length);
            return _template.Substring(_mark.Value, end - _mark.Value);
        }

        private string DebuggerToString()
        {
            if (_index == -1)
            {
                return _template;
            }
            else if (_mark.HasValue)
            {
                return _template.Substring(0, _mark.Value) +
                    "|" +
                    _template.Substring(_mark.Value, _index - _mark.Value) +
                    "|" +
                    _template.Substring(_index);
            }
            else
            {
                return string.Concat(_template.Substring(0, _index), "|", _template.Substring(_index));
            }
        }
    }
}
