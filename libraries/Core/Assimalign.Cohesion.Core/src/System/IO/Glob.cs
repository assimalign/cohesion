using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace System.IO;

using static System.IO.Glob;

using Assimalign.Cohesion.Internal;
using static Assimalign.Cohesion.Internal.PathHelper;

/// <summary>
/// Represents a glob pattern.
/// </summary>
[DebuggerDisplay("{ToString()}")]
public sealed class Glob : IEquatable<Glob>, IEnumerable<Segment>
{
    private readonly Segment[] _segments;

    private Glob(Segment[] segments)
    {
        _segments = segments;
    }

    /// <summary>
    /// Gets the number of segments in the glob pattern.
    /// </summary>
    public int Count => _segments.Length;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public Segment this[int index] => _segments[index];

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public Segment[] GetSegments()
    {
        return [.. _segments]; // Return a difference reference
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public bool Equals(Glob? other)
    {
        ThrowHelper.ThrowIfNull(other, nameof(other));

        var left = ToString();
        var right = other.ToString();

        return string.Equals(left, right);
    }

    public IEnumerator<Segment> GetEnumerator()
    {
        return (IEnumerator<Segment>)_segments.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public static Glob Parse(string pattern)
    {
        ThrowHelper.ThrowIfNullOrEmpty(pattern);

        var isParentSegmentLegal = true;

        var items =  Normalize(pattern).Split(FileSystemPath.Separator);

        var segments = new Segment[items.Length];

        for (int i = 0; i < items.Length; i++)
        {
            Segment segment = null!;

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

        return new Glob(segments);


        static string Portion(string pattern, int beginIndex, int endIndex)
        {
            return pattern.Substring(beginIndex, endIndex - beginIndex);
        }
        static int NextIndex(string pattern, char[] anyOf, int beginIndex, int endIndex)
        {
            int index = pattern.IndexOfAny(anyOf, beginIndex, endIndex - beginIndex);
            return index == -1 ? endIndex : index;
        }
    }


    private static string Normalize(string path)
    {
        var (start, end) = GetTrimRange(path);

        int resize = 0;

        var value = string.Create((end + 1) - start, path, (span, value) =>
        {
            char previous = default;

            // Let's convert all backward slashes to forward slashes
            for (int i = start; i < (end + 1); i++)
            {
                var current = value[i];

                // Convert back slash to forward slash
                if (current == '\\')
                {
                    current = '/';
                }
                // Check for excessive slashes
                if (previous == '/' && current == '/')
                {
                    resize++;
                    continue;
                }

                previous = current;
                span[i - start - resize] = current;
            }
        });

        if (resize > 0)
        {
            return value.Remove(value.Length - resize);
        }
        else
        {
            return value;
        }
    }

    #region Overloads

    public override string ToString()
    {
        return string.Join(FileSystemPath.Separator, [.. _segments]);
    }

    #endregion

    #region Operators

    public static implicit operator Glob(string pattern) => Parse(pattern);

    public static implicit operator string(Glob glob) => glob.ToString();

    #endregion

    #region Partials

    public enum SegmentKind
    {
        /// <summary>
        /// 
        /// </summary>
        Current,

        /// <summary>
        /// 
        /// </summary>
        Literal,

        /// <summary>
        /// * - Matches any character in a filename.
        /// </summary>
        Wildcard,

        /// <summary>
        /// ** - Matches any character in a filename or directory.
        /// </summary>
        RecursiveWildcard,

        /// <summary>
        /// .. - Matches the parent directory.
        /// </summary>
        ParentDirectory,

        /// <summary>
        /// {*.cs,*test} - Matches
        /// </summary>
        BraceGrouping,

        /// <summary>
        /// [abc] or [a-z] or [0-9]
        /// </summary>
        CharacterSet
    }

    public abstract class Segment
    {
        // This should be in accessable
        internal Segment()
        {
        }

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
        public abstract SegmentKind Kind { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public abstract bool IsMatch(string value, StringComparison comparison);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="kind"></param>
        /// <returns></returns>
        public bool IsKind(SegmentKind kind)
        {
            return Kind == kind;
        }
    }
    internal partial class CurrentSegment : Segment
    {
        public override bool HasStem => false;
        public override string Value { get; } = ".";
        public override SegmentKind Kind => SegmentKind.Current;
        public override bool IsMatch(string value, StringComparison comparison)
        {
            return false;
        }
    }
    internal partial class LiteralSegment : Segment
    {
        public LiteralSegment(string value)
        {
            ThrowHelper.ThrowIfNullOrEmpty(value, nameof(value));
            Value = value;
        }

        public override string Value { get; } = "*";
        public override bool HasStem => false;
        public override SegmentKind Kind => SegmentKind.Literal;
        public override bool IsMatch(string value, StringComparison comparison)
        {
            return string.Equals(Value, value, comparison);
        }
    }
    internal partial class ParentSegment : Segment
    {
        public override bool HasStem => false;
        public override string Value { get; } = "..";
        public override SegmentKind Kind => SegmentKind.ParentDirectory;
        public override bool IsMatch(string value, StringComparison comparison)
        {
            return string.Equals(Value, value, comparison);
        }
    }
    internal partial class RecursiveWildcardSegment : Segment
    {
        public override bool HasStem => true;
        public override string Value { get; } = "**";
        public override SegmentKind Kind => SegmentKind.RecursiveWildcard;
        public override bool IsMatch(string value, StringComparison comparison)
        {
            return false;
        }
    }
    internal partial class WildcardSegment : Segment
    {
        public WildcardSegment(string beginsWith, List<string> contains, string endsWith)
        {
            BeginsWith = beginsWith;
            Contains = contains;
            EndsWith = endsWith;
        }

        public override bool HasStem => true;
        public override string Value { get; } = "*";
        public override SegmentKind Kind => SegmentKind.Wildcard;
        public string BeginsWith { get; }
        public string EndsWith { get; }
        public IReadOnlyList<string> Contains { get; }

        public override string ToString()
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrEmpty(BeginsWith))
            {
                builder.Append(BeginsWith);
                builder.Append(Value);
            }

            if (builder.Length == 0 && Contains.Count > 0)
            {
                builder.Append(Value);
            }

            for (int i = 0; i < Contains.Count; i++)
            {
                builder.Append(Contains[i]);
                builder.Append(Value);
            }

            if (!string.IsNullOrEmpty(BeginsWith))
            {
                builder.Append(BeginsWith);
            }

            return builder.ToString();
        }

        public override bool IsMatch(string value, StringComparison comparison)
        {
            WildcardSegment wildcard = this;

            if (value.Length < wildcard.BeginsWith.Length + wildcard.EndsWith.Length)
            {
                return false;
            }

            if (!value.StartsWith(wildcard.BeginsWith, comparison))
            {
                return false;
            }

            if (!value.EndsWith(wildcard.EndsWith, comparison))
            {
                return false;
            }

            int beginRemaining = wildcard.BeginsWith.Length;
            int endRemaining = value.Length - wildcard.EndsWith.Length;
            for (int containsIndex = 0; containsIndex != wildcard.Contains.Count; ++containsIndex)
            {
                string containsValue = wildcard.Contains[containsIndex];
                int indexOf = value.IndexOf(
                    value: containsValue,
                    startIndex: beginRemaining,
                    count: endRemaining - beginRemaining,
                    comparisonType: comparison);
                if (indexOf == -1)
                {
                    return false;
                }

                beginRemaining = indexOf + containsValue.Length;
            }

            return true;
        }


        // It doesn't matter which StringComparison type is used in this MatchAll segment because
        // all comparing are skipped since there is no content in the segment.
        public static readonly WildcardSegment MatchAll = new WildcardSegment(
            string.Empty,
            new List<string>(),
            string.Empty);
    }
    internal partial class BraceGroupingSegment : Segment
    {
        public BraceGroupingSegment(string value, Segment[] groupings)
        {
            Value = value;
            Groupings = groupings;
        }

        public override bool HasStem => false;
        public override string Value { get; }
        public Segment[] Groupings { get; }
        public override SegmentKind Kind => SegmentKind.BraceGrouping;
        public override bool IsMatch(string value, StringComparison comparison)
        {
            foreach (var segment in Groupings)
            {
                if (segment.IsMatch(value, comparison))
                {
                    return true;
                }
            }

            return false;
        }

        #region Overloads
        public override string ToString()
        {
            var builder = new StringBuilder();

            builder.Append("{");

            for (int i = 0; i < Groupings.Length; i++)
            {
                builder.Append(Groupings[i].ToString());

                if (i < Groupings.Length - 1)
                {
                    builder.Append(",");
                }
            }

            builder.Append("}");

            return builder.ToString();
        }

        #endregion
    }
    internal partial class CharacterSetSegment : Segment
    {
        public override bool HasStem => throw new NotImplementedException();

        public override string Value => throw new NotImplementedException();

        public override SegmentKind Kind => SegmentKind.CharacterSet;

        public override bool IsMatch(string value, StringComparison comparison)
        {
            throw new NotImplementedException();
        }
    }
    #endregion
}
