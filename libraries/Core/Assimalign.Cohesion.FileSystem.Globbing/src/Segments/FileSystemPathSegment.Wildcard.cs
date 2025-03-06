using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

public abstract partial class FileSystemPathSegment
{
    internal partial class WildcardSegment : FileSystemPathSegment
    {
        public WildcardSegment(string beginsWith, List<string> contains, string endsWith)
        {
            BeginsWith = beginsWith;
            Contains = contains;
            EndsWith = endsWith;
        }

        public override string Value { get; }
        public override bool HasStem => true;
        public override PathSegmentKind Kind => PathSegmentKind.Wildcard;
        public string BeginsWith { get; }
        public string EndsWith { get; }
        public IReadOnlyList<string> Contains { get; }

        public override bool Match(string value, StringComparison comparison)
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
}