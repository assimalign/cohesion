using System;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// The configuration <see cref="Key"/> is a representation of a composite key within a key-value pair. 
/// <para>Format Examples:</para>
/// <list type="bullet">
/// <item>
///     <term>Slash Format</term>
///     <description>"/key1/"</description>
/// </item>
/// <item>
///     <term>Backward Slash Format</term>
///     <description>"\\key1\\key2[index]\\key3"</description>
/// </item>
/// <item>
///     <term>Namespace Format</term>
///     <description>"key1.key2[index].key3</description>
/// </item>
/// <item>
///     <term>Colon Format</term>
///     <description>"key1:key2[index]:key3"</description>
/// </item>
/// <item>
///     <term>Mixed Format</term>
///     <description>"/key1.key2\\key3[index]:key4"</description>
/// </item>
/// <item>
///     <term>Colon Format (labels)</term>
///     <description>"key1$label1:key2$label2[index]:key3$label3"</description>
/// </item>
/// </list>
/// </summary>
[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(KeyJsonConverter))]
public readonly struct Key : IEquatable<Key>, IEnumerable<KeySegment>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="segments"></param>
    public Key(KeySegment[] segments)
    {
        Segments = segments;
    }

    #region Properties/Fields

    /// <summary>
    /// Get the key parts.
    /// </summary>
    public readonly KeySegment[] Segments { get; }

    /// <summary>
    /// Returns the default separator used within a composite key.
    /// </summary>
    public const char DefaultDelimiter = ':';

    /// <summary>
    /// Allowed separators.
    /// </summary>
    public static readonly char[] Delimiters = ['\\', '/', ':', '.'];

    /// <summary>
    /// Gets an empty key.
    /// </summary>
    public static readonly Key Empty = "";

    #endregion

    #region Methods

    /// <summary>
    /// Returns a new key instance from the last segment of the key.
    /// </summary>
    /// <returns></returns>
    public Key GetLastSegment()
    {
        if (Segments.Length == 0)
        {
            return this;
        }
        return new Key([Segments[Segments.Length - 1]]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start">The starting index.</param>
    /// <returns></returns>
    public Key Subkey(int start)
    {
        return Subkey(start, Segments.Length - start);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public Key Subkey(int start, int length)
    {
        if (start > Segments.Length || start < 0)
        {
            ThrowHelper.ThrowArgumentException("");
        }
        if (length > Segments.Length || length < 1)
        {
            ThrowHelper.ThrowArgumentException("");
        }

        var segments = new KeySegment[length];

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Segments[start + i];
        }

        return new Key(segments);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public Key Combine(Key other)
    {
        return Combine(this, other);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public bool StartsWith(Key key)
    {
        return Equals(key, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public bool StartsWith(Key key, KeyComparison comparison)
    {
        var ls = Segments;
        var rs = key.Segments;

        if (rs.Length > ls.Length)
        {
            return false;
        }
        for (int i = 0; i < rs.Length; i++)
        {
            if (!rs[i].Equals(ls[i], comparison))
            {
                return false;
            }
        }
        return true;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public bool EndsWith(Key key)
    {
        return EndsWith(key, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(Key key, KeyComparison comparison)
    {
        var ls = Segments;
        var rs = key.Segments;

        if (rs.Length > ls.Length)
        {
            return false;
        }

        var l = ls.Length;

        for (int i = rs.Length; i < rs.Length; i--)
        {
            //if (!rs[i .Equals(ls[i], comparison))
            //{
            //    return false;
            //}
        }

        return true;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(Key other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(Key other, KeyComparison comparison)
    {
        return Equals(this, other, comparison);
    }
    #endregion

    #region Overloads
    /// <summary>
    /// Returns a formatted key value.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return string.Join(DefaultDelimiter, Segments);
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    public override bool Equals(object? obj)
    {
        if (obj is Key key)
        {
            return Equals(key);
        }
        return false;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public static bool Equals(Key left, Key right, KeyComparison comparison)
    {
        var ls = left.Segments;
        var rs = right.Segments;

        if (ls.Length != rs.Length)
        {
            return false;
        }
        for (int i = 0; i < ls.Length; i++)
        {
            if (!ls[i].Equals(rs[i], comparison))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Key Combine(Key left, Key right)
    {
        return new Key([.. left.Segments, .. right.Segments]);
    }

    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Key Parse(string? value)
    {
        return Parse(value.AsSpan());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>

    public static Key Parse(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return Empty;
        }

        int start = 0;
        int count = 0;
        KeySegment[] segments = new KeySegment[5];

        while (start < span.Length)
        {
            // Find the next segment by locating ':'
            int segmentEnd = span.Slice(start).IndexOfAny(Delimiters);

            if (segmentEnd == -1)
            {
                segmentEnd = span.Length - start;

                Array.Resize(ref segments, count + 1);
            }

            ReadOnlySpan<char> segment = span.Slice(start, segmentEnd);

            start += segmentEnd + 1; // Move start past the current segment

            // Parse the segment
            segments[count] = KeySegment.Parse(segment);

            count++;

            if (count > segments.Length)
            {
                Array.Resize(ref segments, count + 5);
            }
        }

        return new Key(segments);
    }

    public IEnumerator<KeySegment> GetEnumerator()
    {
        return (IEnumerator<KeySegment>)Segments.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator string(Key key) => key.ToString();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator Key(string value) => Key.Parse(value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Key operator +(Key left, Key right) => Combine(left, right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key left, Key right) => left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key left, Key right) => !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key? left, Key right) => left.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key? left, Key right) => left.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key left, Key? right) => right.HasValue && left.Equals(right);

    public static bool operator !=(Key left, Key? right)
    {
        return right.HasValue && !left.Equals(right);
    }
    //public static bool operator ==(Key? left, Key? right)
    //{
    //    return right.HasValue && left.Equals(right);
    //}
    //public static bool operator !=(Key? left, Key? right)
    //{
    //    return right.HasValue && !left.Equals(right);
    //}
    #endregion


    partial class KeyJsonConverter : JsonConverter<Key>
    {
        public override Key Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Key expected a string token type.");
            }

            var str = reader.GetString();

            if (str is null || str == string.Empty)
            {
                return Key.Empty;
            }

            return Key.Parse(str);
        }

        public override void Write(Utf8JsonWriter writer, Key value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}





//var count = 0;
//var start = 0;
//var length = 0;
//var segments = new KeySegment[5];

//for (int i = 0; i < span.Length; i++)
//{
//    for (int a = 0; a < Delimiters.Length; a++)
//    {
//        var isLast = (i + 1) == span.Length;

//        if (span[i] == Delimiters[a] || isLast)
//        {
//            length = i - start;

//            var segment = span.Slice(start, length);

//            string? item = null;
//            string? label = null;
//            int index = -1;
//            var se = segment.Length;

//            for (int b = 0; b < segment.Length; b++)
//            {
//                var ch = segment[b];

//                if (ch == KeySegment.LabelSeparator)
//                {
//                    se = b;

//                    var s = b + 1;
//                    var e = -1;
//                    for (; b < segment.Length && (ch = segment[b]) != '['; b++)
//                    {
//                        e++;
//                    }

//                    label = segment.Slice(s, e).ToString();
//                }
//                if (ch == '[')
//                {
//                    if (label is null)
//                    {
//                        se = b;
//                    }

//                    var num = segment.Slice(b + 1, segment.Length - (b + 2));

//                    if (!int.TryParse(num, out index))
//                    {
//                        throw new ArgumentException();
//                    }
//                }
//            }

//            item = segment.Slice(0, se).ToString();

//            if (count >= segment.Length)
//            {
//                Array.Resize(ref segments, count + 5);
//            }

//            segments[count] = new KeySegment(item, label!, index);

//            start = (i + 1);
//            count++;

//            if (isLast)
//            {
//                Array.Resize(ref segments, count);

//                return new Key(segments);
//            }
//        }
//    }
//}

//return Key.Empty;


//return new Key(segments);

//var ranges = new Span<Range>(new Range[MaxSegmentLength]);
//var count = span.SplitAny(ranges, Separators);
//var segments = new KeySegment[count];

//for (int i = 0; i < count; i++)
//{
//    var (start, length) = ranges[i].GetStartLength();

//    var segment = span.Slice(start, length);
//    var indexOfLabel = segment.IndexOf('$');
//    var indexOfIndexer = segment.IndexOf('[');

//    if (indexOfIndexer > -1)
//    {
//        var number = segment.Slice(indexOfIndexer + 1, segment.Length - (indexOfIndexer + 3));

//        if (!int.TryParse(number, out var index))
//        {
//            ThrowHelper.ThrowArgumentException("");
//        }
//        if (indexOfLabel > -1)
//        {
//            segments[i] = new KeySegment(
//                segment.Slice(0, indexOfLabel).ToString(),
//                segment.Slice(indexOfLabel + 1, segment.Length - indexOfIndexer + 1).ToString(),
//                index);
//        }
//        else
//        {
//            segments[i] = new KeySegment(
//                segment.Slice(0, indexOfIndexer - 1).ToString(),
//                index);
//        }
//    }
//    else if (indexOfLabel > -1)
//    {
//        segments[i] = new KeySegment(
//            segment.Slice(0, indexOfLabel).ToString(),
//            segment.Slice(indexOfLabel + 1, segment.Length - indexOfLabel - 1).ToString());
//    }
//    else
//    {
//        segments[i] = new KeySegment(segment.ToString());
//    }
//}




// var last = 0;
//var count = 0;
//var segments = 

//for (int i = 0; i < span.Length; i++)
//{
//    var isLast = (i + 1) == key.Length;

//    if (Separators.Contains(span[i]) || isLast)
//    {
//if (isLast) i++;

//var items = new Span<Range>(new Range[25]);
//var segment = span.Slice(last, i - last);
//var count = segment.SplitAny(items, Separators);


//segment.Split()

//for (int a = 0; a < segment.Length; a++)
//{
//    var c = segment[a];

//    if (c == '$')
//    {
//        var start = a + 1;

//        for (; a < segment.Length; a++)
//        {
//            if ((c = segment[a]) == '[')
//            {
//                var end = a;

//                for (; a < segment.Length; i++)
//                {

//                }
//            }
//        }

//        segments[count] = new KeySegment(start)
//    }
//    if (c == '[')
//    {
//        break;
//    }
//}




//        // Get index Label identifier, if any
//        var labelId = segment.IndexOf(KeySegment.LabelSeparator);

//        // Get index of indexer, if any
//        var opening = segment.IndexOf('[');
//        var closing = segment.IndexOf(']');

//        // Parse Indexer 
//        if (opening > -1)
//        {
//            var number = segment.Slice(opening + 1, (closing - 1) - opening).ToString();
//            var isValid =
//                opening <= closing &&                   // Weird, but check if closing bracket comes before opening
//                closing == (segment.Length - 1) &&      // The indexer must be at the end of the string
//                opening != 0                            // First char in segment must be a name
//                ;

//            if (!isValid || !int.TryParse(number, out var index))
//            {
//                ThrowHelper.ThrowArgumentException($"The key segment '{value}' has an invalid indexer.");
//            }

//            // If there is not a label set the end length of the value
//            else if (labelId == -1)
//            {
//                segments[count] = new KeySegment(
//                    segment.Slice(0, opening).ToString(),
//                    index);
//            }
//            else
//            {
//                if (opening > -1 && labelId > opening)
//                {
//                    ThrowHelper.ThrowArgumentException("Label identifiers must come before indexers.");
//                }

//                segments[count] = new KeySegment(
//                    segment.Slice(0, labelId).ToString(),
//                    segment.Slice(labelId + 1, opening - labelId - 2).ToString(),
//                    index);
//            }
//        }
//        // Parse Label
//        else if (labelId > -1)
//        {
//            if (opening > -1 && labelId > opening)
//            {
//                ThrowHelper.ThrowArgumentException("Label identifiers must come before indexers.");
//            }
//            segments[count] = new KeySegment(
//                segment.Slice(0, labelId).ToString(),
//                segment.Slice(labelId + 1, segment.Length - (labelId + 1)).ToString());
//        }
//        else
//        {
//            segments[count] = new KeySegment(segment.ToString());
//        }

//        count++;
//        last = i + 1;
//    }
//}

//if (segments.Length != count)
//{
//    Array.Resize(ref segments, count);
//}

// return new Key(segments);