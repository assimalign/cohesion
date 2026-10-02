using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace System.IO;

using Assimalign.Cohesion.Internal;
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

    ///// <summary>
    ///// The max path length.
    ///// </summary>
    //public const int MaxLength = 4096;

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

    /// <summary>
    /// Returns the path as a read only span of characters.
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
        ReadOnlySpan<char> span = _value.AsSpan();

        List<string> segments = new List<string>();
        int start = 0;

        if (HasRoot(out string root))
        {
            span = span.Slice(root.Length);
            var trimmed = root.TrimEnd('/');
            segments.Add(trimmed.Length > 0 ? trimmed : "/"); // add root as first segment
        }

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
    /// 
    /// </summary>
    /// <param name="start"></param>
    /// <returns></returns>
    public FileSystemPath Subpath(int start)
    {
        return Subpath(start, _value.Length - start);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    public FileSystemPath Subpath(int start, int length)
    {
        return AsSpan().Slice(start, length);
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
    /// <param name="other"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public FileSystemPath Join(FileSystemPath other)
    {
        return Join(this, other);
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
            throw new ArgumentException("The right most path must not be rooted. This includes '[Drive]:/', '//[Server]/[share]', '/'.");
        }

        if (left.Equals("/"))
        {
            return left._value + right._value;
        }

        return string.Join(Separator, left._value, right._value.Trim(Separator));
    }

    /// <summary>
    /// Merges <paramref name="other"/> onto the current path by navigation. See
    /// <see cref="Merge(FileSystemPath, FileSystemPath, CultureInfo, bool)"/> for the rules; the
    /// prefix match is case-sensitive.
    /// </summary>
    /// <param name="other">The path to merge onto the current path.</param>
    /// <returns>The merged path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="other"/> is rooted and does not lie under the current path, or its leading
    /// <c>..</c> segments climb above the current path's root.
    /// </exception>
    public FileSystemPath Merge(FileSystemPath other)
    {
        return Merge(this, other, CultureInfo.InvariantCulture, false);
    }

    /// <summary>
    /// Merges <paramref name="other"/> onto the current path by navigation. See
    /// <see cref="Merge(FileSystemPath, FileSystemPath, CultureInfo, bool)"/> for the rules; the
    /// prefix match is case-sensitive.
    /// </summary>
    /// <param name="other">The path to merge onto the current path.</param>
    /// <param name="cultureInfo">Retained for source compatibility; the prefix match is ordinal.</param>
    /// <returns>The merged path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="other"/> is rooted and does not lie under the current path, or its leading
    /// <c>..</c> segments climb above the current path's root.
    /// </exception>
    public FileSystemPath Merge(FileSystemPath other, CultureInfo cultureInfo)
    {
        return Merge(this, other, cultureInfo, false);
    }

    /// <summary>
    /// Merges <paramref name="other"/> onto the current path by navigation. See
    /// <see cref="Merge(FileSystemPath, FileSystemPath, CultureInfo, bool)"/> for the rules.
    /// </summary>
    /// <param name="other">The path to merge onto the current path.</param>
    /// <param name="cultureInfo">Retained for source compatibility; the prefix match is ordinal.</param>
    /// <param name="ignoreCase"><see langword="true"/> to match the prefix ignoring case.</param>
    /// <returns>The merged path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="other"/> is rooted and does not lie under the current path, or its leading
    /// <c>..</c> segments climb above the current path's root.
    /// </exception>
    public FileSystemPath Merge(FileSystemPath other, CultureInfo cultureInfo, bool ignoreCase)
    {
        return Merge(this, other, cultureInfo, ignoreCase);
    }

    /// <summary>
    /// Merges <paramref name="right"/> onto <paramref name="left"/> by navigation. See
    /// <see cref="Merge(FileSystemPath, FileSystemPath, CultureInfo, bool)"/> for the rules; the
    /// prefix match is case-sensitive.
    /// </summary>
    /// <param name="left">The base path.</param>
    /// <param name="right">The path to merge onto <paramref name="left"/>.</param>
    /// <returns>The merged path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="right"/> is rooted and does not lie under <paramref name="left"/>, or its
    /// leading <c>..</c> segments climb above the root of <paramref name="left"/>.
    /// </exception>
    public static FileSystemPath Merge(FileSystemPath left, FileSystemPath right)
    {
        return Merge(left, right, CultureInfo.InvariantCulture, false);
    }

    /// <summary>
    /// Merges <paramref name="right"/> onto <paramref name="left"/> by navigation: a path that
    /// already lies under <paramref name="left"/> on a segment boundary is returned as is, leading
    /// <c>..</c> segments climb from <paramref name="left"/> toward its root, and any other
    /// relative path is joined onto <paramref name="left"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Merge</c> navigates; it does not confine. A <c>..</c> segment may climb above
    /// <paramref name="left"/> — <c>Merge("/srv/public", "../secret.txt")</c> is
    /// <c>/srv/secret.txt</c> — and stops only at the root of <paramref name="left"/> (its drive,
    /// leading separator, or UNC share), where it throws. Code that must keep a path inside a
    /// directory resolves it and checks the result itself: every Cohesion file-system provider
    /// does, and refuses a path outside its root with <c>FileSystemErrorCode.PathOutsideRoot</c>.
    /// </para>
    /// <para>
    /// The prefix match is ordinal, or ordinal ignore-case when <paramref name="ignoreCase"/> is
    /// <see langword="true"/>, and requires a segment boundary: <c>/srv/public2</c> does not lie
    /// under <c>/srv/public</c>. <paramref name="cultureInfo"/> does not take part in the match. A
    /// culture-aware prefix match can succeed across ignorable characters with a matched length that
    /// does not line up with a separator, so it cannot decide a segment boundary; the parameter is
    /// kept for source compatibility.
    /// </para>
    /// <para>
    /// Only an exact <c>..</c> segment is a parent reference; a name such as <c>..config</c> is an
    /// ordinary segment. A merged path keeps the root of <paramref name="left"/> exactly, so
    /// climbing from <c>/srv/public</c> never produces a <c>//srv</c> (UNC-shaped) path.
    /// </para>
    /// </remarks>
    /// <param name="left">The base path.</param>
    /// <param name="right">The path to merge onto <paramref name="left"/>.</param>
    /// <param name="cultureInfo">Retained for source compatibility; the prefix match is ordinal.</param>
    /// <param name="ignoreCase"><see langword="true"/> to match the prefix ignoring case.</param>
    /// <returns>The merged path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="right"/> is rooted and does not lie under <paramref name="left"/>, or its
    /// leading <c>..</c> segments climb above the root of <paramref name="left"/>.
    /// </exception>
    public static FileSystemPath Merge(FileSystemPath left, FileSystemPath right, CultureInfo cultureInfo, bool ignoreCase)
    {
        if (StartsWithParentSegment(right.AsSpan()))
        {
            return Navigate(left.AsSpan(), right.AsSpan());
        }

        if (IsUnder(right.AsSpan(), left.AsSpan(), ignoreCase))
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
        return EndsWith(path, CultureInfo.InvariantCulture, false);
    }

    /// <summary>
    /// Checks whether the current path ends with provided path.
    /// </summary>
    /// <param name="path">A relative path.</param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path, CultureInfo cultureInfo)
    {
        return EndsWith(path, cultureInfo, false);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="cultureInfo"></param>
    /// <param name="ignoreCase"></param>
    /// <returns></returns>
    public bool EndsWith(FileSystemPath path, CultureInfo cultureInfo, bool ignoreCase)
    {
        return _value.EndsWith(path._value, ignoreCase, cultureInfo);
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
        return StartsWith(path, cultureInfo, false);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="cultureInfo"></param>
    /// <param name="ignoreCase"></param>
    /// <returns></returns>
    public bool StartsWith(FileSystemPath path, CultureInfo cultureInfo, bool ignoreCase)
    {
        return _value.StartsWith(path, ignoreCase, cultureInfo);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int GetHashCode(CultureInfo cultureInfo, bool ignoreCase)
    {
        int code = GetComparer(cultureInfo, ignoreCase).GetHashCode(_value);
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
        return Equals(other, cultureInfo, false);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool Equals(FileSystemPath other, CultureInfo cultureInfo, bool ignoreCase)
    {
        return GetComparer(cultureInfo, ignoreCase).Equals(_value, other._value);
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
        return CompareTo(other, cultureInfo);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="cultureInfo"></param>
    /// <param name="ignoreCase"></param>
    /// <returns></returns>
    public int CompareTo(FileSystemPath path, CultureInfo cultureInfo, bool ignoreCase)
    {
        return GetComparer(cultureInfo, ignoreCase).Compare(_value, path._value);
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
        ArgumentNullException.ThrowIfNull(value);

        return Parse(value.AsSpan());
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

                ArgumentException.ThrowIf(
                    (s < 0 && e > end) || (hasStart && hasEnd),
                    "Parent directory globing is not allowed - \"..\". The value must be an absolute or relative path.");
            }

            ArgumentException.ThrowIf(
                !IsValidPathChar(current),
                $"Path contains illegal character '{current}' at index {i}.");

            previous = current;

            var index = (i + shift) - start - reduce;

            span[index] = current;
        }

        if (reduce > 0)
        {
            span = span.Slice(0, span.Length - reduce);
        }

        //if (span.Length > MaxLength)
        //{
        //    throw new PathTooLongException($"The path length exceed the maximum length of {MaxLength}.");
        //}

        return new FileSystemPath(span.ToString());
    }

    public static FileSystemPath Create(DirectoryName[] names)
    {
        ArgumentNullException.ThrowIfNullOrNone(names);

        if (names.Length == 1)
        {
            return names[0];
        }

        int length = names.Sum(name => name.Length) - 1; // -1 to remove ending '/' from last name

        return new FileSystemPath(string.Create(length, names, (span, items) =>
        {
            int position = 0;
            for (int i = 0; i < items.Length; i++)
            {
                var name = items[i];
                if ((i + 1) == items.Length)
                {
                    // remove ending '/' from last name
                    name.AsSpan().Slice(0, name.Length - 1).CopyTo(span.Slice(position));
                }
                else
                {
                    name.AsSpan().CopyTo(span.Slice(position));
                }
                position += name.Length;
            }
        }));

    }

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
        return GetHashCode(CultureInfo.InvariantCulture, false);
    }

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


    private StringComparer GetComparer(CultureInfo cultureInfo, bool ignoreCase)
    {
        return StringComparer.Create(cultureInfo, ignoreCase);
    }

    private const string mergeAboveRootMessage = "The path cannot be merged. The relative path goes beyond the root of the current path.";

    // True when the path begins with an exact ".." segment. Parse admits ".." only at the start of
    // a relative path, so these leading segments are the only parent references Merge can meet.
    private static bool StartsWithParentSegment(ReadOnlySpan<char> path)
    {
        return path.Length >= 2
            && IsDot(path[0])
            && IsDot(path[1])
            && (path.Length == 2 || IsPathSeparator(path[2]));
    }

    // True when path is prefix itself or lies under it on a segment boundary. A prefix that is a
    // root ending in a separator ("/", "C:/") or a bare drive ("C:") is followed directly by the
    // next segment, so any path that starts with it lies under it.
    private static bool IsUnder(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix, bool ignoreCase)
    {
        if (prefix.IsEmpty)
        {
            return true;
        }

        if (!path.StartsWith(prefix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == prefix.Length || IsPathSeparator(prefix[^1]))
        {
            return true;
        }

        if (prefix.Length == 2 && prefix[1] == ':' && IsValidDriveChar(prefix[0]))
        {
            return true;
        }

        return IsPathSeparator(path[prefix.Length]);
    }

    // Applies the leading ".." segments of right to left, one segment each, never removing the root
    // of left, then appends what remains of right.
    private static FileSystemPath Navigate(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        int rootLength = GetRootLength(left);
        ReadOnlySpan<char> kept = left;

        while (kept.Length > rootLength && IsPathSeparator(kept[^1]))
        {
            kept = kept[..^1];
        }

        while (StartsWithParentSegment(right))
        {
            ArgumentException.ThrowIf(kept.Length <= rootLength, mergeAboveRootMessage);

            int separator = kept.LastIndexOfAny('/', '\\');
            kept = separator < rootLength ? kept[..rootLength] : kept[..separator];
            right = right.Length > 2 ? right[3..] : ReadOnlySpan<char>.Empty;
        }

        if (right.IsEmpty)
        {
            return Parse(kept);
        }

        if (kept.IsEmpty)
        {
            return Parse(right);
        }

        // A root that ends in a separator ("/", "C:/") or a bare drive ("C:") takes the next
        // segment directly; anything else ("/srv", "//server/share") needs a separator first.
        bool bareRoot = kept.Length == rootLength && (IsPathSeparator(kept[^1]) || kept[^1] == ':');

        return Parse(bareRoot
            ? string.Concat(kept, right)
            : string.Concat(kept, "/", right));
    }

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