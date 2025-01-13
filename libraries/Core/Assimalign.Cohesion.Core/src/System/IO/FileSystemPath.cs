using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;


/// <summary>
/// A case-insensitive representation of a file path.
/// </summary>
/// <remarks>
/// Comparing file paths as strings can be dangerous as different OS's have 
/// case-sensitive file system such as linux. The following approach can be 
/// done with a class, or struct as well.
/// </remarks>
[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(PathJsonConverter))]
public readonly struct FileSystemPath : 
    IEquatable<FileSystemPath>, 
    IEqualityComparer<FileSystemPath>, 
    IComparable<FileSystemPath>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<FileSystemPath, FileSystemPath, bool>
    ,IAdditionOperators<FileSystemPath, FileSystemPath, FileSystemPath>
#endif
{
    /* Known Roots:
     * - /
     * - //
     * - \
     * -\\
     * - {Drive}:\
     * - {Drive}:/
     */
    //private static readonly string[] RootPaths = ["/", "\\", "//"];

    private readonly string path;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public FileSystemPath(string path)
    {
        if (path is null || path.Length == 0)
        {
            ThrowHelper.ThrowArgumentNullException($"path cannot be null or empty.");
        }
        if (Path.GetInvalidPathChars().Intersect(path).Any())
        {
            ThrowHelper.ThrowArgumentException($"path contains illegal characters.");
        }
        //if (path.Length > MaxLength)
        //{
        //    ThrowHelper.ThrowArgumentException($"path is too large. Max length is {MaxLength}");
        //}

        int end = path.Length - 1;
        int start = 0;
        char[] trimChars = ['/', '\\'];

        for (start = 0; start < path.Length; start++)
        {
            int num2 = 0;
            char c = path[start];
            for (num2 = 0; num2 < trimChars.Length && trimChars[num2] != c; num2++)
            {
            }
            if (num2 == trimChars.Length)
            {
                break;
            }
        }
        for (end = path.Length - 1; end >= start; end--)
        {
            int num3 = 0;
            char c2 = path[end];
            for (num3 = 0; num3 < trimChars.Length && trimChars[num3] != c2; num3++)
            {
            }
            if (num3 == trimChars.Length)
            {
                break;
            }
        }
        this.path = string.Create((end + 1) - start, path, (span, value) =>
        {
            // Let's convert all forward slashes to backward slashes
            for (int i = start; i < (end + 1); i++)
            {
                var c = value[i];

                if (c == '/')
                {
                    span[i - start] = '\\';
                }
                else
                {
                    span[i - start] = c;
                }
            }
        });
    }

    ///// <summary>
    ///// The max path length allowed.
    ///// </summary>
    //public const int MaxLength = 4096;
    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public char this[int index] => path[index];
    /// <summary>
    /// The length of the path.
    /// </summary>
    public int Length => path.Length;
    /// <summary>
    /// An empty path.
    /// </summary>
    public static FileSystemPath Empty => "\\";
    /// <summary>
    /// The directory separator.
    /// </summary>
    public static char Separator => Path.DirectorySeparatorChar;
    ///// <summary>
    ///// Returns the segments 
    ///// </summary>
    ///// <returns></returns>
    //public IEnumerable<FileSystemPath> GetSegments()
    //{
    //    int a = 0;

        
    //    for (int i = 0; i < path.Length; i++)
    //    {
    //        if (path[i] == '\\')
    //        {
    //            var buffer = new char[i - a];

    //            path.Substring

    //            Array.Copy(path, a, buffer, 0, buffer.Length);

    //            yield return new FileSystemPath(buffer);

    //            a = i;
    //        }
    //        // Check if at the end
    //        else if ((i + 1) == chars.Length)
    //        {
    //            var buffer = new char[chars.Length - a];

    //            Array.Copy(chars, a, buffer, 0, buffer.Length);

    //            yield return new FileSystemPath(buffer);
    //        }
    //    }
    //}
    /// <summary>
    /// Gets the file name, if any.
    /// </summary>
    /// <returns></returns>
    public string? GetFileName()
    {
        return Path.GetFileName(path);
    }
    /// <summary>
    /// Gets the directory name, if any.
    /// </summary>
    /// <returns></returns>
    public string? GetDirectoryName()
    {
        return Path.GetDirectoryName(path);
    }
    /// <summary>
    /// Combines the provided path to the 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public FileSystemPath Combine(FileSystemPath path)
    {
        return Combine([this, path]);
    }
    /// <summary>
    /// Combines an array of paths together.
    /// </summary>
    /// <param name="paths"></param>
    /// <returns></returns>
    public static FileSystemPath Combine(params FileSystemPath[] paths)
    {
        return Path.Combine([.. paths]);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public bool EndsWith(string value)
    {
        return path.EndsWith(value);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(string value, StringComparison comparison)
    {
        return path.EndsWith(value, comparison);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public bool StartsWith(string value)
    {
        return path.StartsWith(value);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(string value, StringComparison comparison)
    {
        return path.StartsWith(value, comparison);
    }

    #region Overloads
    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is FileSystemPath path ? Equals(path) : false;
    }
    /// <inheritdoc />
    public override string ToString()
    {
        return path;
    }
    /// <inheritdoc />
    public override int GetHashCode()
    {
        int code = ToString().GetHashCode(StringComparison.InvariantCultureIgnoreCase);

        return (int)((uint)code | ((uint)code << 16));
    }
    #endregion

    #region Interfaces
    public bool Equals(FileSystemPath other)
    {
        return string.Equals(this, other);
    }
    public bool Equals(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right);
    }
    public int GetHashCode([DisallowNull] FileSystemPath obj)
    {
        return obj.GetHashCode();
    }
    public int CompareTo(FileSystemPath other)
    {
        return string.Compare(this, other);
    }
    #endregion

    #region Operators
    public static implicit operator FileSystemPath(string path)
    {
        return new(path);
    }
    public static implicit operator string(FileSystemPath path)
    {
        return path.ToString();
    }
    public static bool operator ==(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right);
    }
    public static bool operator !=(FileSystemPath left, FileSystemPath right)
    {
        return !left.Equals(right);
    }
    public static FileSystemPath operator +(FileSystemPath left, FileSystemPath right)
    {
        return left.Combine(right);
    }
    #endregion

    #region Converters
    partial class PathJsonConverter : JsonConverter<FileSystemPath>
    {
        public override FileSystemPath Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("");
            }

            var str = reader.GetString();

            if (str is null || str == string.Empty)
            {
                return FileSystemPath.Empty;
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