using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using Assimalign.Cohesion.FileSystem.Globbing.PathSegments;
using Assimalign.Cohesion.FileSystem.Globbing.PatternContexts;

public class FilePatternBuilder
{
    private static readonly char[] slashes = new[] { '/', '\\' };
    private static readonly char[] star = new[] { '*' };

    public FilePatternBuilder()
    {
        ComparisonType = StringComparison.OrdinalIgnoreCase;
    }

    public FilePatternBuilder(StringComparison comparisonType)
    {
        ComparisonType = comparisonType;
    }

    public StringComparison ComparisonType { get; }

    public IFilePattern Build(string pattern)
    {
        if (pattern == null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        pattern = pattern.TrimStart(slashes);

        if (pattern.TrimEnd(slashes).Length < pattern.Length)
        {
            // If the pattern end with a slash, it is considered a
            // a directory.
            pattern = pattern.TrimEnd(slashes) + "/**";
        }

        var allSegments = new List<IFilePathSegment>();
        var isParentSegmentLegal = true;

        IList<IFilePathSegment> segmentsPatternStartsWith = null;
        IList<IList<IFilePathSegment>> segmentsPatternContains = null;
        IList<IFilePathSegment> segmentsPatternEndsWith = null;

        var endPattern = pattern.Length;
        
        for (var scanPattern = 0; scanPattern < endPattern;)
        {
            var beginSegment = scanPattern;
            var endSegment = NextIndex(pattern, slashes, scanPattern, endPattern);

            IFilePathSegment segment = null;

            if (segment == null && endSegment - beginSegment == 3)
            {
                if (pattern[beginSegment] == '*' &&
                    pattern[beginSegment + 1] == '.' &&
                    pattern[beginSegment + 2] == '*')
                {
                    // turn *.* into *
                    beginSegment += 2;
                }
            }

            if (segment == null && endSegment - beginSegment == 2)
            {
                if (pattern[beginSegment] == '*' &&
                    pattern[beginSegment + 1] == '*')
                {
                    // recognized **
                    segment = new RecursiveWildcardSegment();
                }
                else if (pattern[beginSegment] == '.' &&
                         pattern[beginSegment + 1] == '.')
                {
                    // recognized ..

                    if (!isParentSegmentLegal)
                    {
                        throw new ArgumentException("\"..\" can be only added at the beginning of the pattern.");
                    }
                    segment = new FilePathParentSegment();
                }
            }

            if (segment == null && endSegment - beginSegment == 1)
            {
                if (pattern[beginSegment] == '.')
                {
                    // recognized .
                    segment = new FilePathCurrentSegment();
                }
            }

            if (segment == null && endSegment - beginSegment > 2)
            {
                if (pattern[beginSegment] == '*' &&
                    pattern[beginSegment + 1] == '*' &&
                    pattern[beginSegment + 2] == '.')
                {
                    // recognize **.
                    // swallow the first *, add the recursive path segment and
                    // the remaining part will be treat as wild card in next loop.
                    segment = new RecursiveWildcardSegment();
                    endSegment = beginSegment;
                }
            }

            if (segment == null)
            {
                string beginsWith = string.Empty;
                var contains = new List<string>();
                string endsWith = string.Empty;

                for (int scanSegment = beginSegment; scanSegment < endSegment;)
                {
                    int beginLiteral = scanSegment;
                    int endLiteral = NextIndex(pattern, star, scanSegment, endSegment);

                    if (beginLiteral == beginSegment)
                    {
                        if (endLiteral == endSegment)
                        {
                            // and the only bit
                            segment = new FilePathLiteralSegment(Portion(pattern, beginLiteral, endLiteral), ComparisonType);
                        }
                        else
                        {
                            // this is the first bit
                            beginsWith = Portion(pattern, beginLiteral, endLiteral);
                        }
                    }
                    else if (endLiteral == endSegment)
                    {
                        // this is the last bit
                        endsWith = Portion(pattern, beginLiteral, endLiteral);
                    }
                    else
                    {
                        if (beginLiteral != endLiteral)
                        {
                            // this is a middle bit
                            contains.Add(Portion(pattern, beginLiteral, endLiteral));
                        }
                        else
                        {
                            // note: NOOP here, adjacent *'s are collapsed when they
                            // are mixed with literal text in a path segment
                        }
                    }

                    scanSegment = endLiteral + 1;
                }

                if (segment == null)
                {
                    segment = new FilePathWildcardSegment(beginsWith, contains, endsWith, ComparisonType);
                }
            }

            if (segment is not FilePathParentSegment)
            {
                isParentSegmentLegal = false;
            }
            if (segment is FilePathCurrentSegment)
            {
                // ignore ".\"
            }
            else
            {
                if (segment is RecursiveWildcardSegment)
                {
                    if (segmentsPatternStartsWith == null)
                    {
                        segmentsPatternStartsWith = new List<IFilePathSegment>(allSegments);
                        segmentsPatternEndsWith = new List<IFilePathSegment>();
                        segmentsPatternContains = new List<IList<IFilePathSegment>>();
                    }
                    else if (segmentsPatternEndsWith.Count != 0)
                    {
                        segmentsPatternContains.Add(segmentsPatternEndsWith);
                        segmentsPatternEndsWith = new List<IFilePathSegment>();
                    }
                }
                else if (segmentsPatternEndsWith != null)
                {
                    segmentsPatternEndsWith.Add(segment);
                }

                allSegments.Add(segment);
            }

            scanPattern = endSegment + 1;
        }

        if (segmentsPatternStartsWith == null)
        {
            return new LinearPattern(allSegments);
        }
        else
        {
            return new RaggedPattern(allSegments, segmentsPatternStartsWith, segmentsPatternEndsWith, segmentsPatternContains);
        }
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

    private sealed class LinearPattern : IFileLinearPattern
    {
        public LinearPattern(List<IFilePathSegment> allSegments)
        {
            Segments = allSegments;
        }

        public IList<IFilePathSegment> Segments { get; }

        public IFilePatternContext CreatePatternContextForInclude()
        {
            return new FilePatternContextLinearInclude(this);
        }

        public IFilePatternContext CreatePatternContextForExclude()
        {
            return new FilePatternContextLinearExclude(this);
        }
    }
    private sealed class RaggedPattern : IFileRaggedPattern
    {
        public RaggedPattern(List<IFilePathSegment> allSegments, IList<IFilePathSegment> segmentsPatternStartsWith, IList<IFilePathSegment> segmentsPatternEndsWith, IList<IList<IFilePathSegment>> segmentsPatternContains)
        {
            Segments = allSegments;
            StartsWith = segmentsPatternStartsWith;
            Contains = segmentsPatternContains;
            EndsWith = segmentsPatternEndsWith;
        }

        public IList<IList<IFilePathSegment>> Contains { get; }

        public IList<IFilePathSegment> EndsWith { get; }

        public IList<IFilePathSegment> Segments { get; }

        public IList<IFilePathSegment> StartsWith { get; }

        public IFilePatternContext CreatePatternContextForInclude()
        {
            return new FilePatternContextRaggedInclude(this);
        }

        public IFilePatternContext CreatePatternContextForExclude()
        {
            return new FilePatternContextRaggedExclude(this);
        }
    }
}