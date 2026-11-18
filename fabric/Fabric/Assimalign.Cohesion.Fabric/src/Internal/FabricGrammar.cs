using System;

namespace Assimalign.Cohesion.Fabric.Internal;

/// <summary>
/// The character grammars of the fabric's identifiers. Allocation-free span checks, so the value
/// objects validate without regular expressions.
/// </summary>
internal static class FabricGrammar
{
    private const int maxLabelLength = 63;

    /// <summary>
    /// A DNS-style label: 1 to 63 of [a-z0-9-], starting with a letter, not ending with a hyphen.
    /// </summary>
    public static bool IsLabel(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > maxLabelLength || !char.IsAsciiLetterLower(value[0]) || value[^1] == '-')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One or more labels separated by single dots.
    /// </summary>
    public static bool IsDottedLabels(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var range in value.Split('.'))
        {
            if (!IsLabel(value[range]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Dot-separated PascalCase segments: each starts with an uppercase letter and holds only
    /// ASCII letters and digits.
    /// </summary>
    public static bool IsProviderNamespace(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var range in value.Split('.'))
        {
            var segment = value[range];
            if (segment.IsEmpty || !char.IsAsciiLetterUpper(segment[0]))
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// A camelCase type segment: starts with a lowercase letter, then ASCII letters and digits.
    /// </summary>
    public static bool IsTypeSegment(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetterLower(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One of the address forms documented on <see cref="ResourceId"/>.
    /// </summary>
    public static bool IsResourceId(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '/')
        {
            return false;
        }

        Span<Range> ranges = stackalloc Range[9];
        var count = value[1..].Split(ranges, '/');
        if (count > 8)
        {
            return false;
        }

        var segments = value[1..];
        if (count < 2 || !segments[ranges[0]].SequenceEqual("environments") || !IsLabel(segments[ranges[1]]))
        {
            return false;
        }

        if (count == 2)
        {
            return true;
        }

        if (count == 4)
        {
            var collection = segments[ranges[2]];
            return (collection.SequenceEqual("groups") || collection.SequenceEqual("applications") || collection.SequenceEqual("operations"))
                && IsLabel(segments[ranges[3]]);
        }

        return count == 8
            && segments[ranges[2]].SequenceEqual("applications")
            && IsLabel(segments[ranges[3]])
            && segments[ranges[4]].SequenceEqual("providers")
            && IsProviderNamespace(segments[ranges[5]])
            && IsTypeSegment(segments[ranges[6]])
            && IsDottedLabels(segments[ranges[7]]);
    }
}
