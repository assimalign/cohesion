using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The value of a header or trailer field: a single string, or the several values of a repeated field
/// kept apart until <see cref="Value"/> comma-joins them.
/// </summary>
/// <remarks>
/// Appending one value with <see cref="Concat(in HttpHeaderValue, string)"/> costs amortized constant
/// time: past four values the backing array grows geometrically, and a later append fills a spare slot
/// instead of copying every value again. The value stays immutable. A spare slot belongs to the first
/// value appended into it, and any other append from the same original copies, so two values appended
/// to one original never see each other's.
/// </remarks>
[DebuggerDisplay("{Value}")]
public readonly partial struct HttpHeaderValue :
    IList<string?>,
    IReadOnlyList<string?>,
    IEquatable<HttpHeaderValue>,
    IEquatable<string?>,
    IEquatable<string?[]?>
{
    // Arrays with spare capacity start at five values; four and fewer keep an exact array.
    private const int maxExactAppendLength = 4;

    private readonly object? _values;

    // The number of live elements when Concat grew _values into an array with spare capacity. Zero means
    // the whole array is live, which is the case for every array a caller supplies. Read through
    // GetLiveLength, never directly.
    private readonly int _count;

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHeaderValue"/> structure using the specified string.
    /// </summary>
    /// <param name="value">A string value or <c>null</c>.</param>
    public HttpHeaderValue(string? value)
    {
        _values = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHeaderValue"/> structure using the specified array of strings.
    /// </summary>
    /// <param name="values">A string array.</param>
    public HttpHeaderValue(string?[]? values)
    {
        _values = values;
    }

    // An array Concat grew: only the first count elements belong to this value.
    private HttpHeaderValue(string?[] values, int count)
    {
        _values = values;
        _count = count;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the header value rendered as a comma-separated string.
    /// </summary>
    public string Value => GetStringValue() ?? string.Empty;

    public bool IsEmpty
    {
        get
        {
            if (_values is null)
            {
                return true;
            }
            if (_values is string str && string.IsNullOrEmpty(str))
            {
                return true;
            }
            if (_values is string[] strArr && GetLiveLength(strArr, _count) == 0)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the number of <see cref="string"/> elements contained in this <see cref="HttpHeaderValue" />.
    /// </summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
            object? value = _values;
            if (value is null)
            {
                return 0;
            }
            if (value is string)
            {
                return 1;
            }
            else
            {
                // Not string, not null, can only be string[]
                return GetLiveLength(Unsafe.As<string?[]>(value), _count);
            }
        }
    }

    bool ICollection<string?>.IsReadOnly => true;

    /// <summary>
    /// Gets the <see cref="string"/> at index.
    /// </summary>
    /// <value>The string at the specified index.</value>
    /// <param name="index">The zero-based index of the element to get.</param>
    /// <exception cref="NotSupportedException">Set operations are not supported on readonly <see cref="HttpHeaderValue"/>.</exception>
    string? IList<string?>.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets the <see cref="string"/> at index.
    /// </summary>
    /// <value>The string at the specified index.</value>
    /// <param name="index">The zero-based index of the element to get.</param>
    public string? this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
            object? value = _values;
            if (value is string str)
            {
                if (index == 0)
                {
                    return str;
                }
            }
            else if (value != null)
            {
                // Not string, not null, can only be string[]. Past the live length, an array Concat grew
                // holds values that belong to other values appended to the same original.
                string?[] values = Unsafe.As<string?[]>(value);

                if ((uint)index < (uint)GetLiveLength(values, _count))
                {
                    return values[index];
                }
            }

            return OutOfBounds(); // throws
        }
    }

    /// <summary>
    /// A readonly instance of the <see cref="HttpHeaderValue"/> struct whose value is an empty string array.
    /// </summary>
    /// <remarks>
    /// In application code, this field is most commonly used to safely represent a <see cref="HttpHeaderValue"/> that has null string values.
    /// </remarks>
    public static readonly HttpHeaderValue Empty = new HttpHeaderValue(Array.Empty<string>());

    #endregion

    #region Methods

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string OutOfBounds()
    {
        return Array.Empty<string>()[0]; // throws
    }

    /// <summary>
    /// Gets how many elements of <paramref name="values"/> belong to the value: <paramref name="count"/>
    /// when <see cref="Concat(in HttpHeaderValue, string)"/> grew the array with spare capacity, otherwise
    /// the whole array. A count that does not fit the array, which only a torn read of a value overwritten
    /// while it was read can produce, falls back to the array's length rather than reading past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetLiveLength(string?[] values, int count)
    {
        return (uint)(count - 1) < (uint)values.Length ? count : values.Length;
    }

    private string? GetStringValue()
    {
        // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
        object? value = _values;
        if (value is string s)
        {
            return s;
        }
        else
        {
            return GetStringValueFromArray(value, _count);
        }

        static string? GetStringValueFromArray(object? value, int count)
        {
            if (value is null)
            {
                return null;
            }

            Debug.Assert(value is string[]);
            // value is not null or string, array, can only be string[]
            string?[] values = Unsafe.As<string?[]>(value);
            int liveLength = GetLiveLength(values, count);
            return liveLength switch
            {
                0 => null,
                1 => values[0],
                _ => GetJoinedStringValueFromArray(values, liveLength),
            };
        }

        static string GetJoinedStringValueFromArray(string?[] values, int liveLength)
        {
            // Calculate final length
            int length = 0;
            for (int i = 0; i < liveLength; i++)
            {
                string? value = values[i];
                // Skip null and empty values
                if (value != null && value.Length > 0)
                {
                    if (length > 0)
                    {
                        // Add separator
                        length++;
                    }

                    length += value.Length;
                }
            }
            // Create the new string
            return string.Create(length, (values, liveLength), static (span, state) => {
                (string?[] strings, int stringCount) = state;
                int offset = 0;
                // Skip null and empty values
                for (int i = 0; i < stringCount; i++)
                {
                    string? value = strings[i];
                    if (value != null && value.Length > 0)
                    {
                        if (offset > 0)
                        {
                            // Add separator
                            span[offset] = ',';
                            offset++;
                        }

                        value.AsSpan().CopyTo(span.Slice(offset));
                        offset += value.Length;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Creates a string array from the current <see cref="HttpHeaderValue"/> object.
    /// </summary>
    /// <returns>A string array represented by this instance.</returns>
    /// <remarks>
    /// The returned array is always a defensive copy — never the internal backing array —
    /// so callers cannot mutate header state in place through it. Read-only header views
    /// (for example the one handed to request-parse interceptors) rely on this: handing out
    /// the live backing array would let multi-valued headers be rewritten around the
    /// collection's fail-loud read-only guard.
    /// </remarks>
    public string?[] ToArray()
    {
        // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
        object? value = _values;
        if (value is string[] values)
        {
            // Only the live elements: an array Concat grew carries spare slots past them.
            int liveLength = GetLiveLength(values, _count);
            return liveLength == 0 ? Array.Empty<string>() : values.AsSpan(0, liveLength).ToArray();
        }

        if (value != null)
        {
            // value not array, can only be string
            return new[] { Unsafe.As<string>(value) };
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns the zero-based index of the first occurrence of an item in the <see cref="HttpHeaderValue" />.
    /// </summary>
    /// <param name="item">The string to locate in the <see cref="HttpHeaderValue"></see>.</param>
    /// <returns>the zero-based index of the first occurrence of <paramref name="item" /> within the <see cref="HttpHeaderValue"></see>, if found; otherwise, -1.</returns>
    int IList<string?>.IndexOf(string? item)
    {
        return IndexOf(item);
    }

    private int IndexOf(string? item)
    {
        // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
        object? value = _values;
        if (value is string[] values)
        {
            int liveLength = GetLiveLength(values, _count);
            for (int i = 0; i < liveLength; i++)
            {
                if (string.Equals(values[i], item, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        if (value != null)
        {
            // value not array, can only be string
            return string.Equals(Unsafe.As<string>(value), item, StringComparison.Ordinal) ? 0 : -1;
        }

        return -1;
    }

    /// <summary>Determines whether a string is in the <see cref="HttpHeaderValue" />.</summary>
    /// <param name="item">The <see cref="string"/> to locate in the <see cref="HttpHeaderValue" />.</param>
    /// <returns>true if <paramref name="item">item</paramref> is found in the <see cref="HttpHeaderValue" />; otherwise, false.</returns>
    bool ICollection<string?>.Contains(string? item)
    {
        return IndexOf(item) >= 0;
    }

    /// <summary>
    /// Copies the entire <see cref="HttpHeaderValue" />to a string array, starting at the specified index of the target array.
    /// </summary>
    /// <param name="array">The one-dimensional <see cref="Array" /> that is the destination of the elements copied from. The <see cref="Array" /> must have zero-based indexing.</param>
    /// <param name="arrayIndex">The zero-based index in the destination array at which copying begins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array">array</paramref> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="arrayIndex">arrayIndex</paramref> is less than 0.</exception>
    /// <exception cref="ArgumentException">The number of elements in the source <see cref="HttpHeaderValue"></see> is greater than the available space from <paramref name="arrayIndex">arrayIndex</paramref> to the end of the destination <paramref name="array">array</paramref>.</exception>
    void ICollection<string?>.CopyTo(string?[] array, int arrayIndex)
    {
        CopyTo(array, arrayIndex);
    }

    private void CopyTo(string?[] array, int arrayIndex)
    {
        // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
        object? value = _values;
        if (value is string[] values)
        {
            Array.Copy(values, 0, array, arrayIndex, GetLiveLength(values, _count));
            return;
        }

        if (value != null)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }
            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }
            if (array.Length - arrayIndex < 1)
            {
                throw new ArgumentException(
                    $"'{nameof(array)}' is not long enough to copy all the items in the collection. Check '{nameof(arrayIndex)}' and '{nameof(array)}' length.");
            }

            // value not array, can only be string
            array[arrayIndex] = Unsafe.As<string>(value);
        }
    }

    void ICollection<string?>.Add(string? item) => throw new NotSupportedException();

    void IList<string?>.Insert(int index, string? item) => throw new NotSupportedException();

    bool ICollection<string?>.Remove(string? item) => throw new NotSupportedException();

    void IList<string?>.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection<string?>.Clear() => throw new NotSupportedException();

    /// <summary>
    /// Indicates whether the specified <see cref="HttpHeaderValue"/> contains no string values.
    /// </summary>
    /// <param name="value">The <see cref="HttpHeaderValue"/> to test.</param>
    /// <returns>true if <paramref name="value">value</paramref> contains a single null or empty string or an empty array; otherwise, false.</returns>
    public static bool IsNullOrEmpty(HttpHeaderValue value)
    {
        object? data = value._values;
        if (data is null)
        {
            return true;
        }
        if (data is string[] values)
        {
            return GetLiveLength(values, value._count) switch
            {
                0 => true,
                1 => string.IsNullOrEmpty(values[0]),
                _ => false,
            };
        }
        else
        {
            // Not array, can only be string
            return string.IsNullOrEmpty(Unsafe.As<string>(data));
        }
    }

    /// <summary>
    /// Concatenates two specified instances of <see cref="HttpHeaderValue"/>.
    /// </summary>
    /// <param name="values1">The first <see cref="HttpHeaderValue"/> to concatenate.</param>
    /// <param name="values2">The second <see cref="HttpHeaderValue"/> to concatenate.</param>
    /// <returns>The concatenation of <paramref name="values1"/> and <paramref name="values2"/>.</returns>
    /// <remarks>
    /// Appending a single non-null value costs amortized constant time, as
    /// <see cref="Concat(in HttpHeaderValue, string)"/> does; a longer <paramref name="values2"/> is copied
    /// with <paramref name="values1"/> into a new array.
    /// </remarks>
    public static HttpHeaderValue Concat(HttpHeaderValue values1, HttpHeaderValue values2)
    {
        int count1 = values1.Count;
        int count2 = values2.Count;

        if (count1 == 0)
        {
            return values2;
        }

        if (count2 == 0)
        {
            return values1;
        }

        // A repeated field line arrives as a single value: append it in place when values1 has room.
        if (count2 == 1 && values2[0] is { } single)
        {
            return Concat(values1, single);
        }

        var combined = new string[count1 + count2];
        values1.CopyTo(combined, 0);
        values2.CopyTo(combined, count1);
        return new HttpHeaderValue(combined);
    }

    /// <summary>
    /// Concatenates specified instance of <see cref="HttpHeaderValue"/> with specified <see cref="string"/>.
    /// </summary>
    /// <param name="values">The <see cref="HttpHeaderValue"/> to concatenate.</param>
    /// <param name="value">The <see cref="string" /> to concatenate.</param>
    /// <returns>The concatenation of <paramref name="values"/> and <paramref name="value"/>.</returns>
    /// <remarks>
    /// Amortized constant time, so a field repeated <c>n</c> times combines in time and allocation linear
    /// in <c>n</c>. Up to four values the result is an exact array, as before. Past four the array grows
    /// geometrically, and the next append writes the value into the first spare slot. The slot is claimed
    /// atomically: the first append from a given value takes it, and any other append from that value
    /// copies, on any thread. The values <paramref name="values"/> already holds are never changed.
    /// </remarks>
    public static HttpHeaderValue Concat(in HttpHeaderValue values, string? value)
    {
        if (value == null)
        {
            return values;
        }

        // Take local copy of _values so type checks remain valid even if the StringValues is overwritten in memory
        object? data = values._values;

        if (data is null)
        {
            return new HttpHeaderValue(value);
        }

        if (data is string first)
        {
            return new HttpHeaderValue(new string?[] { first, value });
        }

        // Not string, not null, can only be string[]
        string?[] existing = Unsafe.As<string?[]>(data);
        int count = GetLiveLength(existing, values._count);

        if (count == 0)
        {
            return new HttpHeaderValue(value);
        }

        // A slot past the live length is spare only in an array this method grew, and it is free while it is
        // still null: an append writes a non-null value, and a caller's array never has a live length shorter
        // than the array. Claiming it atomically gives each spare slot exactly one owner.
        if (count < existing.Length && Interlocked.CompareExchange(ref existing[count], value, null) is null)
        {
            return new HttpHeaderValue(existing, count + 1);
        }

        int capacity = count < maxExactAppendLength
            ? count + 1
            : Math.Max(count + 1, (int)Math.Min(2L * count, Array.MaxLength));

        string?[] grown = new string?[capacity];
        Array.Copy(existing, grown, count);
        grown[count] = value;

        return capacity == count + 1
            ? new HttpHeaderValue(grown)
            : new HttpHeaderValue(grown, count + 1);
    }

    /// <summary>
    /// Concatenates specified instance of <see cref="string"/> with specified <see cref="HttpHeaderValue"/>.
    /// </summary>
    /// <param name="value">The <see cref="string" /> to concatenate.</param>
    /// <param name="values">The <see cref="HttpHeaderValue"/> to concatenate.</param>
    /// <returns>The concatenation of <paramref name="values"/> and <paramref name="values"/>.</returns>
    public static HttpHeaderValue Concat(string? value, in HttpHeaderValue values)
    {
        if (value == null)
        {
            return values;
        }

        int count = values.Count;
        if (count == 0)
        {
            return new HttpHeaderValue(value);
        }

        var combined = new string[count + 1];
        combined[0] = value;
        values.CopyTo(combined, 1);
        return new HttpHeaderValue(combined);
    }

    /// <summary>
    /// Determines whether two specified <see cref="HttpHeaderValue"/> objects have the same values in the same order.
    /// </summary>
    /// <param name="left">The first <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The second <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool Equals(HttpHeaderValue left, HttpHeaderValue right)
    {
        int count = left.Count;

        if (count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether this instance and another specified <see cref="HttpHeaderValue"/> object have the same values.
    /// </summary>
    /// <param name="other">The string to compare to this instance.</param>
    /// <returns><c>true</c> if the value of <paramref name="other"/> is the same as the value of this instance; otherwise, <c>false</c>.</returns>
    public bool Equals(HttpHeaderValue other) => Equals(this, other);

    /// <summary>
    /// Determines whether the specified <see cref="string"/> and <see cref="HttpHeaderValue"/> objects have the same values.
    /// </summary>
    /// <param name="left">The <see cref="string"/> to compare.</param>
    /// <param name="right">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>. If <paramref name="left"/> is <c>null</c>, the method returns <c>false</c>.</returns>
    public static bool Equals(string? left, HttpHeaderValue right) => Equals(new HttpHeaderValue(left), right);

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and <see cref="string"/> objects have the same values.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The <see cref="string"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>. If <paramref name="right"/> is <c>null</c>, the method returns <c>false</c>.</returns>
    public static bool Equals(HttpHeaderValue left, string? right) => Equals(left, new HttpHeaderValue(right));

    /// <summary>
    /// Determines whether this instance and a specified <see cref="string"/>, have the same value.
    /// </summary>
    /// <param name="other">The <see cref="string"/> to compare to this instance.</param>
    /// <returns><c>true</c> if the value of <paramref name="other"/> is the same as this instance; otherwise, <c>false</c>. If <paramref name="other"/> is <c>null</c>, returns <c>false</c>.</returns>
    public bool Equals(string? other) => Equals(this, new HttpHeaderValue(other));

    /// <summary>
    /// Determines whether the specified string array and <see cref="HttpHeaderValue"/> objects have the same values.
    /// </summary>
    /// <param name="left">The string array to compare.</param>
    /// <param name="right">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool Equals(string?[]? left, HttpHeaderValue right) => Equals(new HttpHeaderValue(left), right);

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and string array objects have the same values.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The string array to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool Equals(HttpHeaderValue left, string?[]? right) => Equals(left, new HttpHeaderValue(right));

    /// <summary>
    /// Determines whether this instance and a specified string array have the same values.
    /// </summary>
    /// <param name="other">The string array to compare to this instance.</param>
    /// <returns><c>true</c> if the value of <paramref name="other"/> is the same as this instance; otherwise, <c>false</c>.</returns>
    public bool Equals(string?[]? other) => Equals(this, new HttpHeaderValue(other));


    /// <summary>Retrieves an object that can iterate through the individual strings in this <see cref="HttpHeaderValue" />.</summary>
    /// <returns>An enumerator that can be used to iterate through the <see cref="HttpHeaderValue" />.</returns>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(_values, _count);
    }

    /// <inheritdoc cref="GetEnumerator()" />
    IEnumerator<string?> IEnumerable<string?>.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc cref="GetEnumerator()" />
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    #endregion

    #region Operators

    /// <summary>
    /// Defines an implicit conversion of a given string to a <see cref="HttpHeaderValue"/>.
    /// </summary>
    /// <param name="value">A string to implicitly convert.</param>
    public static implicit operator HttpHeaderValue(string? value)
    {
        return new HttpHeaderValue(value);
    }

    /// <summary>
    /// Defines an implicit conversion of a given string array to a <see cref="HttpHeaderValue"/>.
    /// </summary>
    /// <param name="values">A string array to implicitly convert.</param>
    public static implicit operator HttpHeaderValue(string?[]? values)
    {
        return new HttpHeaderValue(values);
    }

    /// <summary>
    /// Defines an implicit conversion of a given <see cref="HttpHeaderValue"/> to a string, with multiple values joined as a comma separated string.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> where <see cref="HttpHeaderValue"/> has been initialized from an empty string array or is <see cref="HttpHeaderValue.Empty"/>.
    /// </remarks>
    /// <param name="values">A <see cref="HttpHeaderValue"/> to implicitly convert.</param>
    public static implicit operator string(HttpHeaderValue values)
    {
        return values.Value;
    }

    /// <summary>
    /// Defines an implicit conversion of a given <see cref="HttpHeaderValue"/> to a string array.
    /// The result is a defensive copy (see <see cref="ToArray"/>), never the internal backing array.
    /// </summary>
    /// <param name="value">A <see cref="HttpHeaderValue"/> to implicitly convert.</param>
    public static implicit operator string?[]?(HttpHeaderValue value)
    {
        // Escaping the backing array of a multi-valued header here would let callers mutate header
        // state in place, bypassing read-only views; ToArray copies the live values.
        return value._values is null ? null : value.ToArray();
    }

    /// <summary>
    /// Determines whether two specified <see cref="HttpHeaderValue"/> have the same values.
    /// </summary>
    /// <param name="left">The first <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The second <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is the same as the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator ==(HttpHeaderValue left, HttpHeaderValue right)
    {
        return Equals(left, right);
    }

    /// <summary>
    /// Determines whether two specified <see cref="HttpHeaderValue"/> have different values.
    /// </summary>
    /// <param name="left">The first <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The second <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is different to the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(HttpHeaderValue left, HttpHeaderValue right)
    {
        return !Equals(left, right);
    }



    /// <inheritdoc cref="Equals(HttpHeaderValue, string)" />
    public static bool operator ==(HttpHeaderValue left, string? right)
    {
        return Equals(left, new HttpHeaderValue(right));
    }

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and <see cref="string"/> objects have different values.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The <see cref="string"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is different to the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(HttpHeaderValue left, string? right)
    {
        return !Equals(left, new HttpHeaderValue(right));
    }

    /// <inheritdoc cref="Equals(string, HttpHeaderValue)" />
    public static bool operator ==(string? left, HttpHeaderValue right)
    {
        return Equals(new HttpHeaderValue(left), right);
    }

    /// <summary>
    /// Determines whether the specified <see cref="string"/> and <see cref="HttpHeaderValue"/> objects have different values.
    /// </summary>
    /// <param name="left">The <see cref="string"/> to compare.</param>
    /// <param name="right">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is different to the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(string left, HttpHeaderValue right)
    {
        return !Equals(new HttpHeaderValue(left), right);
    }

    /// <inheritdoc cref="Equals(HttpHeaderValue, string[])" />
    public static bool operator ==(HttpHeaderValue left, string?[]? right)
    {
        return Equals(left, new HttpHeaderValue(right));
    }

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and string array have different values.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The string array to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is different to the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(HttpHeaderValue left, string?[]? right)
    {
        return !Equals(left, new HttpHeaderValue(right));
    }

    /// <inheritdoc cref="Equals(string[], HttpHeaderValue)" />
    public static bool operator ==(string?[]? left, HttpHeaderValue right) => Equals(new HttpHeaderValue(left), right);

    /// <summary>
    /// Determines whether the specified string array and <see cref="HttpHeaderValue"/> have different values.
    /// </summary>
    /// <param name="left">The string array to compare.</param>
    /// <param name="right">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the value of <paramref name="left"/> is different to the value of <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(string?[]? left, HttpHeaderValue right) => !Equals(new HttpHeaderValue(left), right);

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and <see cref="object"/>, which must be a
    /// <see cref="HttpHeaderValue"/>, <see cref="string"/>, or array of <see cref="string"/>, have the same value.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The <see cref="object"/> to compare.</param>
    /// <returns><c>true</c> if the <paramref name="left"/> object is equal to the <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator ==(HttpHeaderValue left, object? right) => left.Equals(right);

    /// <summary>
    /// Determines whether the specified <see cref="HttpHeaderValue"/> and <see cref="object"/>, which must be a
    /// <see cref="HttpHeaderValue"/>, <see cref="string"/>, or array of <see cref="string"/>, have different values.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The <see cref="object"/> to compare.</param>
    /// <returns><c>true</c> if the <paramref name="left"/> object is equal to the <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(HttpHeaderValue left, object? right) => !left.Equals(right);

    /// <summary>
    /// Determines whether the specified <see cref="object"/>, which must be a
    /// <see cref="HttpHeaderValue"/>, <see cref="string"/>, or array of <see cref="string"/>, and specified <see cref="HttpHeaderValue"/>,  have the same value.
    /// </summary>
    /// <param name="left">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <param name="right">The <see cref="object"/> to compare.</param>
    /// <returns><c>true</c> if the <paramref name="left"/> object is equal to the <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator ==(object? left, HttpHeaderValue right) => right.Equals(left);

    /// <summary>
    /// Determines whether the specified <see cref="object"/> and <see cref="HttpHeaderValue"/> object have the same values.
    /// </summary>
    /// <param name="left">The <see cref="object"/> to compare.</param>
    /// <param name="right">The <see cref="HttpHeaderValue"/> to compare.</param>
    /// <returns><c>true</c> if the <paramref name="left"/> object is equal to the <paramref name="right"/>; otherwise, <c>false</c>.</returns>
    public static bool operator !=(object? left, HttpHeaderValue right) => !right.Equals(left);

    #endregion

    #region Overloads

    /// <summary>
    /// Converts the value of the current <see cref="HttpHeaderValue"/> object to its equivalent string representation, with multiple values joined as a comma separated string.
    /// </summary>
    /// <returns>A string representation of the value of the current <see cref="HttpHeaderValue"/> object.</returns>
    public override string ToString()
    {
        return Value;
    }

    /// <summary>
    /// Determines whether this instance and a specified object have the same value.
    /// </summary>
    /// <param name="obj">An object to compare with this object.</param>
    /// <returns><c>true</c> if the current object is equal to <paramref name="obj"/>; otherwise, <c>false</c>.</returns>
    public override bool Equals(object? obj)
    {
        if (obj == null)
        {
            return Equals(this, HttpHeaderValue.Empty);
        }

        if (obj is string str)
        {
            return Equals(this, str);
        }

        if (obj is string[] array)
        {
            return Equals(this, array);
        }

        if (obj is HttpHeaderValue stringValues)
        {
            return Equals(this, stringValues);
        }

        return false;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        object? value = _values;
        if (value is string[] values)
        {
            int liveLength = GetLiveLength(values, _count);
            if (liveLength == 1)
            {
                return Unsafe.As<string>(values[0])?.GetHashCode() ?? liveLength.GetHashCode();
            }
            int hashCode = 0;
            for (int i = 0; i < liveLength; i++)
            {
                // RyuJIT optimizes this to use the ROL instruction
                // Related GitHub pull request: https://github.com/dotnet/coreclr/pull/1830


                var rol5 = ((uint)hashCode << 5) | ((uint)hashCode >> 27);
                hashCode = ((int)rol5 + hashCode) ^ values[i]?.GetHashCode() ?? 0;
            }
            return hashCode;
        }
        else
        {
            return Unsafe.As<string>(value)?.GetHashCode() ?? Count.GetHashCode();
        }
    }

    #endregion

    #region Partials

    /// <summary>
    /// Enumerates the string values of a <see cref="HttpHeaderValue" />.
    /// </summary>
    public struct Enumerator : IEnumerator<string?>
    {
        private readonly string?[]? _values;
        // The live length of _values: an array Concat grew carries spare slots past it.
        private readonly int _length;
        private int _index;
        private string? _current;

        internal Enumerator(object? value, int count)
        {
            if (value is string str)
            {
                _values = null;
                _length = 0;
                _current = str;
            }
            else
            {
                _current = null;
                _values = Unsafe.As<string?[]>(value);
                _length = _values is null ? 0 : GetLiveLength(_values, count);
            }
            _index = 0;
        }

        /// <summary>
        /// Initializes an enumerator over the string values of <paramref name="values"/>.
        /// </summary>
        /// <param name="values">The value to enumerate.</param>
        public Enumerator(ref HttpHeaderValue values) : this(values._values, values._count)
        { }

        public bool MoveNext()
        {
            int index = _index;
            if (index < 0)
            {
                return false;
            }

            string?[]? values = _values;
            if (values != null)
            {
                if ((uint)index < (uint)_length)
                {
                    _index = index + 1;
                    _current = values[index];
                    return true;
                }

                _index = -1;
                return false;
            }

            _index = -1; // sentinel value
            return _current != null;
        }

        public string? Current => _current;

        object? IEnumerator.Current => _current;

        void IEnumerator.Reset()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
