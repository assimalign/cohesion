using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;
using static Assimalign.Cohesion.Internal.PathHelper;

/// <summary>
/// A case-insensitive representation of an absolute or relative path.
/// </summary>
[DebuggerDisplay("{_value}")]
[JsonConverter(typeof(PathJsonConverter))]
public readonly struct FileSystemPath : IEquatable<FileSystemPath>, IComparable<FileSystemPath>
#if NET7_0_OR_GREATER
    , IEqualityOperators<FileSystemPath, FileSystemPath, bool>
    , IAdditionOperators<FileSystemPath, FileSystemPath, FileSystemPath>
#endif
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

        return Path.IsPathRooted(_value);
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

        var value = Path.GetPathRoot(_value)!;

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

        if (right.HasRoot(out var root) && root.Length != 1 && root[0] != Separator)
        {
            ThrowHelper.ThrowArgumentException("The right most path must not be rooted in either '[Drive]:/' or '//[Server]/[share]'.");
        }

        return string.Join(Separator, left._value, right._value.Trim(Separator));
    }

    /// <summary>
    /// Combines the provided path with the current instance. If the provided path 
    /// partially matches the current instance then the path is merged.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public FileSystemPath Combine(FileSystemPath path)
    {
        return Combine(this, path);
    }

    /// <summary>
    /// Tries to merge the path if 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static FileSystemPath Combine(FileSystemPath left, FileSystemPath right)
    {
        if (right.StartsWith(left))
        {
            return right;
        }

        return Join(right, left);
    }

    /// <summary>
    /// Checks whether the current path ends with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path)
    {
        return EndsWith(path, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks whether the current path ends with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path, CultureInfo cultureInfo)
    {
        return _value.EndsWith(path._value, true, cultureInfo);
    }

    /// <summary>
    /// Checks whether the current path starts with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <returns></returns>
    public bool StartsWith(FileSystemPath path)
    {
        return StartsWith(path, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks whether the current path starts with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(FileSystemPath path, CultureInfo cultureInfo)
    {
        return _value.StartsWith(path._value, true, cultureInfo);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int GetHashCode(CultureInfo cultureInfo)
    {
        int code = StringComparer.Create(cultureInfo, true).GetHashCode(_value);

        return (int)((uint)code | ((uint)code << 16));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(FileSystemPath other)
    {
        return Equals(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool Equals(FileSystemPath other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Equals(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(FileSystemPath other)
    {
        return CompareTo(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int CompareTo(FileSystemPath other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Compare(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public static FileSystemPath Parse(string value)
    {
        ThrowHelper.ThrowIfNullOrEmpty(value, nameof(value));

        // Check if only root was passed
        if (value.Length == 1)
        {
            if (IsPathSeparator(value[0]))
            {
                return new FileSystemPath("/");
            }
            if (IsDot(value[0])) // "." is current directory
            {
                return Empty;
            }
        }

        int start = 0;
        int end = value.Length - 1;
        int shift = 0;

        // Check for current directory syntax "./" and skip over
        if (value.Length >= 2 && IsDot(value[0]) && IsPathSeparator(value[1]))
        {
            start =+ 2;
        }

        // Check if path has valid drive, if so disregard shift
        if (HasDriveLetter(value))
        {
            shift = 0;
        }

        // Check for leading slash root  '//' or '\\', or if your a weirdo '/\' '\/'
        else if (value.Length >= 2 && IsPathSeparator(value[0]) && IsPathSeparator(value[1]))
        {
            shift =+ 2;
        }
        // Maintain directory root '/'
        else if (value.Length >= 2 && IsPathSeparator(value[0]))
        {
            shift = 1;
        }

        CalculateTrimRange(value, ref start, ref end);

        int reduce = 0;
        int length = ((end + 1) - start) + shift;

        var span = new Span<char>(new char[length]);

        for (int i = 0; i < shift; i++)
        {
            span[i] = Separator;
        }

        char previous = default;

        // Let's convert all backward slashes to forward slashes
        for (int i = start; i < (end + 1); i++)
        {
            var current = value[i];

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

            // Check for parent directory globing ".."
            if (IsDot(previous) && IsDot(current))
            {
                // scenario 1: ".." was only passed
                // scenario 2: "{directory}/../{directory}"
                // scenario 3: "../{directory}"
                // scenario 4: "/{directory}/.."

                var s = i - 2;
                var e = i + 1;

                var hasStart = (s > 0 && IsPathSeparator(value[s])) || s < 0;
                var hasEnd = (e < end && IsPathSeparator(value[e])) || e > end;

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

            span[(i + shift) - start - reduce] = current;
        }

       // span[span.Length - reduce]

        if (reduce > 0)
        {
            span = span.Slice(0, span.Length - reduce);
        }

        if (span.Length > MaxLength)
        {
            ThrowHelper.ThrowArgumentException("The path is too long");
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
        return GetHashCode(CultureInfo.InvariantCulture);
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
        return Parse(path);
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
        return left.Combine(right);
    }

    #endregion

    #region Partials

    partial class PathJsonConverter : JsonConverter<FileSystemPath>
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