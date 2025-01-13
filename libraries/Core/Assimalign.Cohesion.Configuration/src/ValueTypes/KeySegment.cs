using System;
using System.Linq;
using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("{ToString()}")]
public readonly struct KeySegment : IEquatable<KeySegment>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public KeySegment(string value)
    {
        ThrowHelper.ThrowIfArgumentNullOrEmpty(value, nameof(value));
        ThrowHelper.ThrowArgumentExceptionIf(value.ContainsAny(Key.Delimiters), "A key segment cannot contain any separators.");
        ThrowHelper.ThrowArgumentExceptionIf(value.Contains(LabelSeparator), "A key segment cannot contain any separators.");

        Value = value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="index"></param>
    public KeySegment(string value, int index) : this(value)
    {
        ThrowHelper.ThrowArgumentExceptionIf(index < -1, "Invalid index. The index value must be greater than or equal to -1.");
        Index = index;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="label"></param>
    public KeySegment(string value, string label) : this(value)
    {
        ThrowHelper.ThrowIfArgumentNullOrEmpty(label, nameof(label));
        Label = label;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="label"></param>
    /// <param name="index"></param>
    public KeySegment(string value, string label, int index) : this(value, label)
    {
        ThrowHelper.ThrowArgumentExceptionIf(index < -1, "Invalid index. The index value must be greater than or equal to -1.");
        Index = index;
    }

    #region Properties

    /// <summary>
    /// Gets the index value, if any. The default is -1.
    /// </summary>
    public int Index { get; } = -1;

    /// <summary>
    /// The raw key value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the label segment. 
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// The separator used to identify a labeled segment.
    /// </summary>
    public const char LabelSeparator = '$';

    #endregion

    #region Methods

    /// <summary>
    /// Returns the int index value, if any.
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public bool HasIndex(out int index)
    {
        return (index = Index) > -1;
    }

    /// <summary>
    ///
    /// </summary>
    /// <remarks>
    /// Labels represent a way to implement polymorphic/dynamic configuration
    /// </remarks>
    /// <param name="label"></param>
    /// <returns></returns>
    public bool HasLabel(out string? label)
    {
        return (label = Label) is not null;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(KeySegment other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public bool Equals(KeySegment other, KeyComparison comparison)
    {
        var comparer = comparison switch
        {
            KeyComparison.Ordinal => StringComparer.Ordinal,
            KeyComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            _ => null
        };

        if (comparer is null)
        {
            throw new ArgumentException("Invalid comparison argument.");
        }
        
        return comparer.Equals(Value, other.Value) &&
               comparer.Equals(Label, other.Label) &&
               Index == other.Index;
    }
    #endregion

    #region Overloads
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        // ReadOnlySpan<char> chars;
        // int length = Value.Length;

        //length.

        string label;
        int index;
        var length = Value.Length;

        if (HasLabel(out label!) && HasIndex(out index))
        {
            return $"{Value}${label}[{index}]";
        }
        if (HasLabel(out label!))
        {
            return $"{Value}${label}";
        }
        if (HasIndex(out index))
        {
            return $"{Value}[{index}]";
        }
        return Value;
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    public override bool Equals(object? obj)
    {
        if (obj is KeySegment segment)
        {
            return Equals(segment);
        }
        return false;
    }
    #endregion


    #region Helpers

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static KeySegment Parse(string value)
    {
        return Parse(value.AsSpan());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="segment"></param>
    /// <returns></returns>
    public static KeySegment Parse(ReadOnlySpan<char> segment)
    {
        ReadOnlySpan<char> value = segment;
        ReadOnlySpan<char> label = ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> index = ReadOnlySpan<char>.Empty;

        // Check for label `$`
        int labelIndex = segment.IndexOf('$');
        if (labelIndex != -1)
        {
            value = segment.Slice(0, labelIndex);
            segment = segment.Slice(labelIndex + 1);

            // Check for index `[]` after the label
            int bracketStart = segment.IndexOf('[');
            if (bracketStart != -1)
            {
                label = segment.Slice(0, bracketStart);
                index = segment.Slice(bracketStart + 1, segment.Length - bracketStart - 2); // Exclude `[]`

                if (!int.TryParse(index, out var number))
                {
                    ThrowHelper.ThrowArgumentException("");
                }

                return new KeySegment(new string(value), new string(label), number);
            }
            else
            {
                label = segment;

                return new KeySegment(new string(value), new string(label));
            }
        }
        else
        {
            // Check for index `[]` without label
            int bracketStart = segment.IndexOf('[');
            if (bracketStart != -1)
            {
                value = segment.Slice(0, bracketStart);
                index = segment.Slice(bracketStart + 1, segment.Length - bracketStart - 2); // Exclude `[]`


                if (!int.TryParse(index, out var number))
                {
                    ThrowHelper.ThrowArgumentException("");
                }

                return new KeySegment(new string(value), number);
            }
        }

        return new KeySegment(new string(value));
    }

    #endregion

    #region Operators
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator KeySegment(string value)
    {
        return Parse(value);
    }

    #endregion
}

//var end = value.Length;

//// Get index Label identifier, if any
//var labelId = value.IndexOf('$');

//// Get index of indexer, if any
//var opening = value.IndexOf('[');
//var closing = value.IndexOf(']');

//// Parse Label
//if (labelId > -1)
//{
//    if (opening > -1 && labelId > opening)
//    {
//        ThrowHelper.ThrowArgumentException("Label identifiers must come before indexers.");
//    }

//    Label = value.Substring(labelId + 1, value.Length - (labelId + 1));

//    end = labelId;
//}
//// Parse Indexer 
//if (opening > -1)
//{
//    int index = 0;
//    var number = value.Substring(opening + 1, (closing - 1) - opening);

//    var isValid =
//        opening <= closing &&               // Weird, but check if closing bracket comes before opening
//        closing == (value.Length - 1) &&    // The indexer must be at the end of the string
//        opening != 0 &&                     // First char in segment must be a name
//        int.TryParse(number, out index);

//    if (!isValid)
//    {
//        ThrowHelper.ThrowArgumentException($"The key segment '{value}' has an invalid indexer.");
//    }

//    Index = index;

//    // If there is not a label set the end length of the value
//    if (labelId == -1)
//    {
//        end = opening;
//    }
//}

//Value = value.Substring(0, end);


// Old Parsing Method

//var name = new char[value.Length];

//for (int i = 0; i < value.Length; i++)
//{
//    // Parse Index
//    if (value[i].Equals('['))
//    {
//        Array.Resize(ref name, i);

//        var closing = value.IndexOf(']');

//        var num = new char[value.Length - 2 - i];

//        for (; value[i] != ']'; i++)
//        {
//            if (value.Length == i)
//            {
//                ThrowHelper.ThrowArgumentException("");
//            }
//        }
//        if (!int.TryParse(num, out var index))
//        {
//            // TODO: Invalid index
//        }
//        Index = index;
//    }
//    // Parse Label
//    else if (value[i].Equals('$'))
//    {
//        Label = value.Substring(i + 1);
//        break;
//    }
//    else
//    {
//        name[i] = value[i];
//    }
//}

//Value = new string(name);