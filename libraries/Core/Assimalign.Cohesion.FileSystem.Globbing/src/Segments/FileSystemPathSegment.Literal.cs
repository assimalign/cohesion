using System;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;

using Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;

public abstract partial class FileSystemPathSegment
{

    internal partial class LiteralSegment : FileSystemPathSegment
    {
        public LiteralSegment(string value)
        {
            ThrowHelper.ThrowIfNullOrEmpty(value, nameof(value));
            Value = value;
        }

        public override string Value { get; } = "*";
        public override bool HasStem => false;
        public override PathSegmentKind Kind => PathSegmentKind.Literal;
        public override bool Match(string value, StringComparison comparison)
        {
            return string.Equals(Value, value, comparison);
        }
    }
    // internal partial class LiteralSegment : FileSystemPathSegment
    //{
    //    private readonly StringComparison _comparisonType;

    //    public LiteralSegment(string value, StringComparison comparisonType)
    //    {
    //        if (value == null)
    //        {
    //            throw new ArgumentNullException(nameof(value));
    //        }

    //        Value = value;

    //        _comparisonType = comparisonType;
    //    }

    //    public string Value { get; }
    //    public override bool HasStem => false;
    //    public override PathSegmentKind Kind => PathSegmentKind.Literal;
    //    public override bool Match(string value)
    //    {
    //        return string.Equals(Value, value, _comparisonType);
    //    }

    //    public override bool Equals(object obj)
    //    {
    //        var other = obj as LiteralSegment;

    //        return other != null &&
    //            _comparisonType == other._comparisonType &&
    //            string.Equals(other.Value, Value, _comparisonType);
    //    }

    //    public override int GetHashCode()
    //    {
    //        return StringComparisonHelper.GetStringComparer(_comparisonType).GetHashCode(Value);
    //    }
    //}
}


