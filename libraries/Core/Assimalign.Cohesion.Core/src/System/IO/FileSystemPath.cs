using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace System.IO;

using Assimalign.Cohesion.Internal;
using System.Buffers;
using System.Collections;
using static Assimalign.Cohesion.Internal.PathHelper;

/// <summary>
/// A case-insensitive representation of an absolute or relative path.
/// </summary>
[DebuggerDisplay("{_value}")]
[JsonConverter(typeof(PathJsonConverter))]
public readonly struct FileSystemPath : IEquatable<FileSystemPath>
    , IComparable<FileSystemPath>
    , IEqualityOperators<FileSystemPath, FileSystemPath, bool>
    , IAdditionOperators<FileSystemPath, FileSystemPath, FileSystemPath>
{
    private readonly string _value;

    private FileSystemPath(string value)
    {
        _value = value;
    }

    /// <summary>
    /// The max path length.
    /// </summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// The directory separator.
    /// </summary>
    public const char Separator = '/';

    /// <summary>
    /// The length of the path.
    /// </summary>
    public int Length => _value.Length;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public char this[int index] => _value[index];

    /// <summary>
    /// Checks whether the path is empty.
    /// </summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>
    /// An empty path.
    /// </summary>
    public static FileSystemPath Empty { get; } = new FileSystemPath("");

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public ReadOnlySpan<char> AsSpan()
    {
        return _value.AsSpan();
    }

    /// <summary>
    /// Returns the segments of the path relative to the root. The root, if any, is disregarded.
    /// </summary>
    /// <returns></returns>
    public string[] GetSegments()
    {
        //if (HasRoot(out string root))
        //{
        //    return _value.Substring(root.Length).Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        //}

        //return _value.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

        ReadOnlySpan<char> span = _value.AsSpan();

        if (HasRoot(out string root))
        {
            span = span.Slice(root.Length);
        }

        var segments = new List<string>();
        int start = 0;

        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == Separator)
            {
                if (i > start)
                {
                    segments.Add(span.Slice(start, i - start).ToString());
                }
                start = i + 1;
            }
        }

        if (start < span.Length)
        {
            segments.Add(span.Slice(start).ToString());
        }

        return segments.ToArray();
    }

    /// <summary>
    /// Checks if the path is rooted.
    /// </summary>
    /// <returns></returns>
    public bool HasRoot()
    {
        if (IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> path = _value.AsSpan();
        int length = path.Length;

        if (length < 1 || !IsDirectorySeparator(path[0]))
        {
            if (length >= 2 && IsValidDriveChar(path[0]))
            {
                return path[1] == ':';
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the root of the path, if any.
    /// </summary>
    /// <param name="root"></param>
    /// <returns></returns>
    public bool HasRoot(out string root)
    {
        root = default!;

        if (IsEmpty)
        {
            return false;
        }

        var value = GetPathRoot(_value)!;

        if (!string.IsNullOrEmpty(value))
        {
            root = string.Create(value.Length, value, (span, item) =>
            {
                for (int i = 0; i < item.Length; i++)
                {
                    if (item[i] == '\\')
                    {
                        span[i] = '/';
                    }
                    else
                    {
                        span[i] = item[i];
                    }
                }
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a valid drive letter is Returns the 
    /// </summary>
    /// <param name="drive">The drive letter</param>
    /// <returns></returns>
    public bool HasDrive(out char drive)
    {
        drive = '\0'; // set it too null char

        if (HasDriveLetter(_value))
        {
            drive = _value[0];

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if the path has a valid share - `//[server]/[share]`
    /// </summary>
    /// <param name="share">Returns the absolute path  of the share.</param>
    /// <returns></returns>
    public bool HasShare(out string share)
    {
        share = null!;

        if (HasRoot(out var root) && root.Length >= 5 && IsPathSeparator(root[0]) && IsPathSeparator(root[1]))
        {
            share = root;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Concatenates the current path and the provided path.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public FileSystemPath Join(FileSystemPath path)
    {
        return Join(this, path);
    }

    /// <summary>
    ///  Concatenates the two paths together.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static FileSystemPath Join(FileSystemPath left, FileSystemPath right)
    {
        if (right.IsEmpty)
        {
            return left;
        }

        if (left.IsEmpty)
        {
            return right;
        }

        if (right.HasRoot())
        {
            ThrowHelper.ThrowArgumentException("The right most path must not be rooted in either '[Drive]:/' or '//[Server]/[share]'.");
        }

        return string.Join(Separator, left._value.Trim(Separator), right._value.Trim(Separator));
    }

    /// <summary>
    /// Combines the provided path with the current instance. If the provided path 
    /// partially matches or the path is relative to the current instance then the path is merged.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public FileSystemPath Merge(FileSystemPath path)
    {
        return Merge(this, path);
    }

    /// <summary>
    /// Tries to merge the path if 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="PathTooLongException"></exception>
    public static FileSystemPath Merge(FileSystemPath left, FileSystemPath right)
    {
        if (right.StartsWith(".."))
        {
            var ls = left.GetSegments();
            var rs = right.GetSegments();

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == "..")
                {
                    if (ls.Length == 0)
                    {
                        ThrowHelper.ThrowArgumentException("The path cannot be merged. The relative path goes beyond the root of the current path.");
                    }
                    ls = ls[..^1];
                }
                else
                {
                    if (left.HasRoot(out var root))
                    {
                        return string.Join(Separator, [root, .. ls, .. rs[i..]]);
                    }
                    else
                    {
                        return string.Join(Separator, [.. ls, .. rs[i..]]);
                    }
                }
            }
        }

        if (right.StartsWith(left))
        {
            return right;
        }

        return Join(left, right);
    }

    /// <summary>
    /// Checks whether the current path ends with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path)
    {
        return EndsWith(path, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// Checks whether the current path ends with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path, StringComparison comparison)
    {
        return _value.EndsWith(path._value, comparison);
    }

    /// <summary>
    /// Checks whether the current path starts with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <returns></returns>
    public bool StartsWith(FileSystemPath path)
    {
        return StartsWith(path, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// Checks whether the current path starts with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(FileSystemPath path, StringComparison comparison)
    {
        return _value.StartsWith(path._value, comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int GetHashCode(StringComparison comparison)
    {
        int code = StringComparer.FromComparison(comparison).GetHashCode(_value);
        return (int)((uint)code | ((uint)code << 16));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(FileSystemPath other)
    {
        return Equals(other, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool Equals(FileSystemPath other, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(FileSystemPath other)
    {
        return CompareTo(other, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int CompareTo(FileSystemPath other, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Compare(_value, other._value);
    }

    /// <summary>
    /// Parses a string value into a <see cref="FileSystemPath"/>.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public static FileSystemPath Parse(string value)
    {
        return Parse(ThrowHelper.ThrowIfNull(value).AsSpan());
    }

    /// <summary>
    /// Parses a string value into a <see cref="FileSystemPath"/>.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public static FileSystemPath Parse(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty)
        {
            return Empty;
        }

        // Check if only root was passed
        if (input.Length == 1)
        {
            // "/" is root directory
            if (IsPathSeparator(input[0]))
            {
                return new FileSystemPath("/");
            }

            // "." is current directory
            if (IsDot(input[0]))
            {
                return Empty;
            }
        }

        // Check for relative path
        if (input.SequenceEqual(".."))
        {
            return new FileSystemPath("..");
        }

        int start = 0;
        int end = input.Length - 1;
        int shift = 0;

        // Check for current directory syntax "./" and skip over
        if (input.Length >= 2 && IsDot(input[0]) && IsPathSeparator(input[1]))
        {
            start += 2;
        }

        // Check if path has valid drive, if so disregard shift
        if (HasDriveLetter(input))
        {
            shift = 0;
        }

        // Check for leading slash root  '//' or '\\', or if your a weirdo '/\' '\/'
        else if (input.Length >= 2 && IsPathSeparator(input[0]) && IsPathSeparator(input[1]))
        {
            shift += 2;
        }

        // Maintain directory root '/'
        else if (input.Length >= 2 && IsPathSeparator(input[0]))
        {
            shift = 1;
        }

        // Trim beginning and ending of 
        CalculateSeparatorTrimRange(input, ref start, ref end);

        int reduce = 0;
        int length = ((end + 1) - start) + shift;

        var span = new Span<char>(new char[length]);

        for (int i = 0; i < shift; i++)
        {
            span[i] = Separator;
        }

        // Skip relative path beginning, if any '../../..'
        if (shift == 0 && start == 0)
        {
            for (; shift < (end + 1); shift += 2)
            {
                if (IsDot(input[shift]) && IsDot(input[shift + 1]))
                {
                    if ((shift + 2) < (end + 1) && !IsPathSeparator(input[shift + 2]))
                    {
                        break;
                    }

                    span[shift] = '.';
                    span[shift + 1] = '.';

                    if ((shift + 2) < (end + 1) && IsPathSeparator(input[shift + 2]))
                    {
                        span[shift + 2] = Separator;
                        shift += 1;
                    }
                }
                else
                {
                    break;
                }
            }

            start += shift;
        }

        char previous = default;

        // Let's convert all backward slashes to forward slashes
        for (int i = start; i < (end + 1); i++)
        {
            var current = input[i];

            // Convert back slash to forward slash
            if (current == '\\')
            {
                current = Separator;
            }

            // Check for excessive slashes
            if (IsPathSeparator(previous) && IsPathSeparator(current))
            {
                reduce++;
                continue;
            }

            // Check for parent directory globing ".." within the path
            // Parent directory is allowed only at the beginning of a relative path
            if (IsDot(previous) && IsDot(current))
            {
                // scenario 1: ".." was only passed
                // scenario 2: "{directory}/../{directory}"
                // scenario 3: "../{directory}"
                // scenario 4: "/{directory}/.."

                var s = i - 2;
                var e = i + 1;

                var hasStart = (s > 0 && IsPathSeparator(input[s])) || s < 0;
                var hasEnd = (e < end && IsPathSeparator(input[e])) || e > end;

                if ((s < 0 && e > end) || (hasStart && hasEnd))
                {
                    ThrowHelper.ThrowArgumentException("Parent directory globing is not allowed - \"..\". The value must be an absolute or relative path.");
                }
            }

            if (!IsValidPathChar(current))
            {
                ThrowHelper.ThrowArgumentException($"Path contains illegal character '{current}' at index {i}.");
            }

            previous = current;

            var index = (i + shift) - start - reduce;

            span[index] = current;
        }

        if (reduce > 0)
        {
            span = span.Slice(0, span.Length - reduce);
        }

        if (span.Length > MaxLength)
        {
            ThrowHelper.ThrowPathToLongException();
        }

        return new FileSystemPath(span.ToString());
    }
    #region Overloads

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is FileSystemPath path)
        {
            return Equals(path);
        }
        return false;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return _value;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return GetHashCode(StringComparison.InvariantCulture);
    }

    #endregion

    #endregion

    #region Operators

    /// <summary>
    /// Implicitly converts a string value into a <see cref="FileSystemPath"/>.
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator FileSystemPath(string path)
    {
        return Parse(path.AsSpan());
    }

    /// <summary>
    /// Implicitly converts a <see cref="FileSystemPath"/> into a string.
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator string(FileSystemPath path)
    {
        return path._value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator FileSystemPath(ReadOnlySpan<char> path)
    {
        return Parse(path);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator ReadOnlySpan<char>(FileSystemPath path)
    {
        return path.AsSpan();
    }

    /// <summary>
    /// Check whether the paths are equal.
    /// </summary>
    /// <param name="left">The left operand of the operator.</param>
    /// <param name="right">The right operand of the operator.</param>
    /// <returns></returns>
    public static bool operator ==(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Check whether the paths are not equal.
    /// </summary>
    /// <param name="left">The left operand of the operator.</param>
    /// <param name="right">The right operand of the operator.</param>
    /// <returns></returns>
    public static bool operator !=(FileSystemPath left, FileSystemPath right)
    {
        return !left.Equals(right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left">The left operand of the operator.</param>
    /// <param name="right">The right operand of the operator.</param>
    /// <returns></returns>
    public static FileSystemPath operator +(FileSystemPath left, FileSystemPath right)
    {
        return left.Merge(right);
    }

    #endregion

    #region Partials

    internal partial class PathJsonConverter : JsonConverter<FileSystemPath>
    {
        public override FileSystemPath Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("");
            }

            var str = reader.GetString();

            if (string.IsNullOrEmpty(str))
            {
                return Empty;
            }

            return new FileSystemPath(str);
        }

        public override void Write(Utf8JsonWriter writer, FileSystemPath value, JsonSerializerOptions options)
        {
            var str = value.ToString();

            writer.WriteStringValue(str);
        }
    }


    //internal ref struct SegmentEnumerable : IEnumerable<ReadOnlySpan<char>>
    //{
    //    private FileSystemPath _path;
    //    internal SegmentEnumerable(FileSystemPath path)
    //    {
    //        _path = path;
    //    }

    //    public IEnumerator<ReadOnlySpan<char>> GetEnumerator()
    //    {
    //        return new SegmentEnumerator(_path);
    //    }

    //    IEnumerator IEnumerable.GetEnumerator()
    //    {
    //        return GetEnumerator();
    //    }
    //}

    //internal ref struct SegmentEnumerator : IEnumerator<ReadOnlySpan<char>>
    //{
    //    private ReadOnlySpan<char> _remaining;
    //    private ReadOnlySpan<char> _current;
    //    private bool _isActive;

    //    private static readonly SearchValues<char> _separator = SearchValues.Create("/".AsSpan());

    //    internal SegmentEnumerator(ReadOnlySpan<char> span)
    //    {
    //        _remaining = span;
    //    }

    //    public ReadOnlySpan<char> Current => _current;

    //    object IEnumerator.Current
    //    {
    //        get
    //        {
    //            throw new NotSupportedException();
    //        }
    //    }

    //    public void Dispose()
    //    {
    //        throw new NotImplementedException();
    //    }

    //    public bool MoveNext()
    //    {
    //        if (!_isActive)
    //        {
    //            _current = default(ReadOnlySpan<char>);
    //            return false;
    //        }
    //        ReadOnlySpan<char> remaining = _remaining;
    //        int num = remaining.IndexOfAny(_separator);
    //        if ((uint)num < (uint)remaining.Length)
    //        {
    //            int num2 = 1;
    //            if (remaining[num] == '\r' && (uint)(num + 1) < (uint)remaining.Length && remaining[num + 1] == '\n')
    //            {
    //                num2 = 2;
    //            }
    //            _current = remaining.Slice(0, num);
    //            _remaining = remaining.Slice(num + num2);
    //        }
    //        else
    //        {
    //            _current = remaining;
    //            _remaining = default(ReadOnlySpan<char>);
    //            _isActive = false;
    //        }
    //        return true;
    //    }

    //    public void Reset()
    //    {
    //        throw new NotImplementedException();
    //    }
    //}

    #endregion
}


// STRATEGY 1
//unsafe
//{
//    fixed (char* value = path)
//    {
//        var span = new Span<char>(value + start, ((end + 1) - start) + shift);

//        for (int i = 0; i < shift; i++)
//        {
//            span[i] = Separator;
//        }

//        char previous = default;

//        // Let's convert all backward slashes to forward slashes
//        for (int i = start; i < (end + 1); i++)
//        {
//            var current = path[i];

//            // Convert back slash to forward slash
//            if (current == '\\')
//            {
//                current = Separator;
//            }

//            // Check for excessive slashes
//            if (IsSeparator(previous) && IsSeparator(current))
//            {
//                reduce++;
//                continue;
//            }

//            // Check for parent directory globing ".."
//            if (IsDot(previous) && IsDot(current))
//            {
//                // scenario 1: ".." was only passed
//                // scenario 2: "{directory}/../{directory}"
//                // scenario 3: "../{directory}"
//                // scenario 4: "/{directory}/.."

//                var s = i - 2;
//                var e = i + 1;

//                var hasStart = (s > 0 && IsSeparator(path[s])) || s < 0;
//                var hasEnd = (e < end && IsSeparator(path[e])) || e > end;

//                if ((s < 0 && e > end) || (hasStart && hasEnd))
//                {
//                    ThrowHelper.ThrowArgumentException("Parent directory globing is not allowed - \"..\". The value must be an absolute or relative path.");
//                }
//            }
//            if (!IsValidPathChar(current))
//            {
//                ThrowHelper.ThrowArgumentException($"Path contains illegal character '{current}' at index {i}.");
//            }

//            previous = current;

//            span[(i + shift) - start - reduce] = current;
//        }

//        if (reduce > 0)
//        {
//            span = span.Slice(0, span.Length - reduce);
//        }

//        return new FileSystemPath(span.ToString());
//    }
//}


// STRATEGY 2
//string? error = null!;

//var value = string.Create(((end + 1) - start) + shift, path, (span, value) =>
//{
//    for (int i = 0; i < shift; i++)
//    {
//        span[i] = Separator;
//    }

//    char previous = default;

//    // Let's convert all backward slashes to forward slashes
//    for (int i = start; i < (end + 1); i++)
//    {
//        var current = value[i];

//        // Convert back slash to forward slash
//        if (current == '\\')
//        {
//            current = Separator;
//        }

//        // Check for excessive slashes
//        if (IsSeparator(previous) && IsSeparator(current))
//        {
//            reduce++;
//            continue;
//        }

//        // Check for parent directory globbing ".."
//        if (IsDot(previous) && IsDot(current))
//        {
//            // scenario 1: ".." was only passed
//            // scenario 2: "{directory}/../{directory}"
//            // scenario 3: "../{directory}"
//            // scenario 4: "/{directory}/.."

//            var s = i - 2;
//            var e = i + 1;

//            var hasStart = (s > 0 && IsSeparator(value[s])) || s < 0;
//            var hasEnd = (e < end && IsSeparator(value[e])) || e > end;

//            if ((s < 0 && e > end) || (hasStart && hasEnd))
//            {
//                error = "Parent directory globbing is not allowed - \"..\". The value must be an absolute or relative path.";
//                break;
//            }
//        }
//        if (!IsValidPathChar(current))
//        {
//            error = $"Path contains illegal character '{current}' at index {i}.";
//            break;
//        }

//        previous = current;

//        span[(i + shift) - start - reduce] = current;
//    }
//});

//if (error is not null)
//{
//    ThrowHelper.ThrowArgumentException(error);
//}

//if (reduce > 0)
//{
//    return new FileSystemPath(value.Remove(value.Length - reduce));
//}
//else
//{
//    return new FileSystemPath(value);
//}