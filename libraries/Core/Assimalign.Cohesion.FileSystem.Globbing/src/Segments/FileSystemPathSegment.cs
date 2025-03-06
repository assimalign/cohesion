using System.Collections.Generic;
using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public abstract partial class FileSystemPathSegment
{
    /// <summary>
    /// 
    /// </summary>
    public abstract bool HasStem { get; }

    /// <summary>
    /// Teh raw segment value.
    /// </summary>
    public abstract string Value { get; }

    /// <summary>
    /// The kind of segment.
    /// </summary>
    public abstract PathSegmentKind Kind { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public abstract bool Match(string value, StringComparison comparison);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static FileSystemPathSegment[] Parse(FileSystemPath pattern)
    {
        var isParentSegmentLegal = true;

        var items = pattern.GetSegments();
        var segments = new FileSystemPathSegment[items.Length];

        for (int i = 0; i < items.Length; i++)
        {
            FileSystemPathSegment segment = null!;

            var current = items[i];

            if (current.Contains('?'))
            {
                throw new Exception("`?` is not supported. Use `*` instead.");
            }

            // Convert `*.*` to `*`
            if (segment is null && current.Length == 3 && current[0] == '*' && current[1] == '.' && current[2] == '*')
            {
                current = "*";
            }

            // Check for `..` or `**`
            if (segment is null && current.Length == 2)
            {
                segment = current switch
                {
                    "**" => new RecursiveWildcardSegment(),
                    ".." when isParentSegmentLegal => new ParentSegment(),
                    ".." when !isParentSegmentLegal => throw new ArgumentException("\"..\" can be only added at the beginning of the pattern."),
                    _ => null!
                };
            }

            // Check for current segment `.`
            if (segment is null && current.Length == 1 && current[0] == '.')
            {
                segment = new CurrentSegment();
            }

            // Check for recursive wild card `**.`
            if (segment is null && current.StartsWith("**."))
            {
                segment = new RecursiveWildcardSegment();
                current = current.Substring(2);
            }

            if (segment is null)
            {
                var beginsWith = string.Empty;
                var endsWith = string.Empty;
                var contains = new List<string>();

                var ending = current.Length;

                for (int scanSegment = 0; scanSegment < ending;)
                {
                    int beginLiteral = scanSegment;
                    int endLiteral = NextIndex(current, ['*'], scanSegment, ending);

                    if (beginLiteral == 0)
                    {
                        if (endLiteral == ending)
                        {
                            // and the only bit
                            segment = new LiteralSegment(Portion(current, beginLiteral, endLiteral));
                        }
                        else
                        {
                            // this is the first bit
                            beginsWith = Portion(pattern, beginLiteral, endLiteral);
                        }
                    }
                    else if (endLiteral == ending)
                    {
                        // this is the last bit
                        endsWith = Portion(current, beginLiteral, endLiteral);
                    }
                    else
                    {
                        if (beginLiteral != endLiteral)
                        {
                            // this is a middle bit
                            contains.Add(Portion(current, beginLiteral, endLiteral));
                        }
                        else
                        {
                            // note: NOOP here, adjacent *'s are collapsed when they
                            // are mixed with literal text in a path segment
                        }
                    }

                    scanSegment = endLiteral + 1;
                }

                if (segment is null)
                {
                    segment = new WildcardSegment(beginsWith, contains, endsWith);
                }
            }

            if (segment is not ParentSegment)
            {
                isParentSegmentLegal = false;
            }
            if (segment is CurrentSegment)
            {
                continue;
            }

            segments[i] = segment;
        }

        return segments;
    }

    private static int NextIndex(string pattern, char[] anyOf, int beginIndex, int endIndex)
    {
        int index = pattern.IndexOfAny(anyOf, beginIndex, endIndex - beginIndex);
        return index == -1 ? endIndex : index;
    }
    private static string Portion(string pattern, int beginIndex, int endIndex)
    {
        return pattern.Substring(beginIndex, endIndex - beginIndex);
    }
}