using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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

    private const string unmatchedOpenBraceError =
        "It ends with an unmatched '{'. Close the parameter with '}', or write a literal '{' as '{{'.";

    private const string optionalParameterPrecededByPeriodHint =
        "In a segment with several parts, only a period ('.') may come directly before an optional parameter, for example '{name}.{ext?}'.";

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
                throw CreateException(pattern, "It contains an empty segment ('//'). Separate path segments with a single '/'.");
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
                var message = "Infinite loop detected in the parser. Please open an issue.";
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
            $"The route template '{pattern}' is invalid. {error ?? "It could not be parsed."}");
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
                    context.Error = unmatchedOpenBraceError;
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
                var message = "Infinite loop detected in the parser. Please open an issue.";
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
                            $"The parameter '{context.MarkedText()}' contains an unescaped '{{'. " +
                            "Inside a parameter, write a literal '{' as '{{'.";
                        return false;
                    }
                }
                else
                {
                    // This is a dangling open-brace, which is not allowed
                    // Example: "{p1:regex(^\d{"
                    context.Error =
                        $"The parameter '{context.MarkedText()}' is not closed: the template ends after an unescaped '{{'. " +
                        "Close the parameter with '}', and write a literal '{' inside it as '{{'.";
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
                    $"The parameter '{context.MarkedText()}' is not closed: the template ends before its closing '}}'. " +
                    "End the parameter with '}'.";
                return false;
            }
        }

        var text = context.Capture();
        if (text == "{}")
        {
            context.Error = "It contains an empty parameter '{}'. Name the parameter, for example '{id}'.";
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
                $"The catch-all parameter '{text}' cannot be optional: a catch-all already matches when the rest of the path is empty. " +
                "Remove the '?'.";
            return false;
        }

        if (templatePart.IsOptional && templatePart.Default != null)
        {
            // Cannot be optional and have a default value.
            // The only way to declare an optional parameter is to have a ? at the end,
            // hence we cannot have both default value and optional parameter within the template.
            // A workaround is to add it as a separate entry in the defaults argument.
            context.Error =
                $"The parameter '{text}' is both optional ('?') and has a default value ('='). " +
                "Use one or the other: a parameter with a default value can already be left out of the path.";
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
                    context.Error = unmatchedOpenBraceError;
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
                    context.Error = "It ends with an unmatched '}'. Write a literal '}' as '}}'.";
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
                        $"The literal text '{context.MarkedText(includeCurrent: false)}' contains an unmatched '}}'. " +
                        "Write a literal '}' as '}}', or open a parameter with '{'.";
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
                        $"The catch-all parameter '{parameter.DebuggerToString()}' must be the last segment of the template, but more segments follow it. " +
                        "Move the catch-all to the end, or remove the segments after it.";
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
                    $"The segment '{RoutePatternPathSegment.DebuggerToString(parts)}' combines the catch-all parameter '{parameter.DebuggerToString()}' with other text. " +
                    "A catch-all parameter must be the only content of its segment.";
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
                            $"In the segment '{RoutePatternPathSegment.DebuggerToString(parts)}', the optional parameter '{parameter.Name}' directly follows '{previousPart.DebuggerToString()}'. " +
                            optionalParameterPrecededByPeriodHint;

                        return false;
                    }
                    else if (previousPart is RoutePatternLiteralSegment literal && literal.Content != PeriodString)
                    {
                        // The optional parameter is preceded by a literal other than period, for example
                        // '{name}-{ext?}'.
                        context.Error =
                            $"In the segment '{RoutePatternPathSegment.DebuggerToString(parts)}', the optional parameter '{parameter.Name}' follows '{literal.Content}'. " +
                            optionalParameterPrecededByPeriodHint;

                        return false;
                    }

                    parts[i - 1] = RoutePatternFactory.SeparatorPart(((RoutePatternLiteralSegment)previousPart).Content);
                }
                else
                {
                    // This optional parameter is not the last one in the segment, for example
                    // '{name?}.{ext}'.
                    context.Error =
                        $"In the segment '{RoutePatternPathSegment.DebuggerToString(parts)}', the optional parameter '{parameter.Name}' is followed by '{parts[i + 1].DebuggerToString()}'. " +
                        "An optional parameter must be the last part of its segment.";

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
                    $"The segment '{RoutePatternPathSegment.DebuggerToString(parts)}' places the parameters '{previous}' and '{current}' next to each other, so the boundary between their values is ambiguous. " +
                    $"Separate them with literal text, for example '{previous}-{current}'.";
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
            context.Error = $"The parameter '{parameterText}' has no name. Name the parameter, for example '{{id}}'.";
            return false;
        }

        int invalidIndex = parameterName.AsSpan().IndexOfAny(InvalidParameterNameChars);
        if (invalidIndex >= 0)
        {
            context.Error =
                $"The parameter name '{parameterName}' contains '{parameterName[invalidIndex]}'. " +
                "A parameter name cannot contain '/', '{', '}', '?' or '*'.";
            return false;
        }

        if (!context.ParameterNames.Add(parameterName))
        {
            context.Error =
                $"The parameter name '{parameterName}' is used more than once. " +
                "Parameter names must be unique within a template, ignoring case.";
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
                $"The literal text '{literal}' contains '?'. A '?' may only end a parameter to make it optional (for example '{{id?}}'); " +
                "a route template never includes a query string.";
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
                "It starts with '~' but not '~/'. Begin the template with '~/', with '/', or with its first path segment.");
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
