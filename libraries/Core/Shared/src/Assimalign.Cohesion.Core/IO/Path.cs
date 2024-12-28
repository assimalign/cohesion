using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion;

using Internal;


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
public readonly struct Path : IEquatable<Path>, IEqualityComparer<Path>, IComparable<Path>
{
    private readonly char[] chars;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <exception cref="ArgumentException"></exception>
    public Path(char[] path)
    {
        if (path is null || path.Length == 0)
        {
            ThrowHelper.ThrowArgumentException($"path cannot be null or empty.");
        }
        if (System.IO.Path.GetInvalidPathChars().Intersect(path).Any())
        {
            ThrowHelper.ThrowArgumentException($"path contains illegal characters.");
        }
        if (path.Length > MaxLength)
        {
            ThrowHelper.ThrowArgumentException($"path is too large. Max length is {MaxLength}");
        }

        // Trim Path characters
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

        this.chars = new char[(end + 1) - start];

        // Let's convert all forward slashes to backward slashes
        for (int i = start; i < (end + 1); i++)
        {
            var c = path[i];

            if (c == '/')
            {
                this.chars[i - start] = '\\';
            }
            else
            {
                this.chars[i - start] = c;
            }
        }
    }

    /// <summary>
    /// The max path length allowed.
    /// </summary>
    public const int MaxLength = 4096;
    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public char this[int index] => chars[index];
    /// <summary>
    /// The length of the path.
    /// </summary>
    public int Length => chars.Length;
    /// <summary>
    /// Gets the file name or directory if it exists.
    /// </summary>
    /// <returns></returns>
    public string? GetDirectoryOrFileName()
    {
        for (int i = (Length - 1); i > 0; i--)
        {
            if (chars[i] == '\\' && i < Length) // if i equals the length then it is an empty string
            {
                var buffer = new char[Length - i - 1];

                for (int a = 0; a < buffer.Length; a++)
                {
                    buffer[a] = chars[i + 1 + a];
                }

                return new string(buffer);
            }
        }

        return null;
    }
    /// <summary>
    /// Combines the provided path to the 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public Path Combine(Path path)
    {
        return Combine([this, path]);
    }
    /// <summary>
    /// Combines an array of paths together.
    /// </summary>
    /// <param name="paths"></param>
    /// <returns></returns>
    public static Path Combine(params Path[] paths)
    {
        var index = 0;
        var buffer = new char[0];

        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i];

            Array.Resize(ref buffer, (buffer.Length + path.Length + 1));

            for (int a = 0; a < path.Length; a++)
            {
                buffer[index] = path[a];
                index++;
            }

            buffer[index] = '\\';
            index++;
        }

        return new Path(buffer);
    }
    /// <summary>
    /// An empty path.
    /// </summary>
    public static Path Empty => "\\";

    #region Overloads
    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is Path path ? Equals(path) : false;
    }
    /// <inheritdoc />
    public override string ToString()
    {
        return new string(chars);
    }
    /// <inheritdoc />
    public override int GetHashCode()
    {
        int code = ToString().GetHashCode(StringComparison.InvariantCultureIgnoreCase);

        return (int)((uint)code | ((uint)code << 16));
    }
    #endregion

    #region Interfaces
    public bool Equals(Path other)
    {
        return string.Equals(
            ToString(),
            other.ToString(),
            StringComparison.InvariantCultureIgnoreCase);
    }
    public bool Equals(Path left, Path right)
    {
        return left.Equals(right);
    }
    public int GetHashCode([DisallowNull] Path obj)
    {
        return obj.GetHashCode();
    }
    public int CompareTo(Path other)
    {
        return string.Compare(
            ToString(),
            other.ToString(),
            StringComparison.InvariantCultureIgnoreCase);
    }
    #endregion

    #region Operators
    public static implicit operator Path(string name)
    {
        return new(name.ToCharArray());
    }
    public static implicit operator string(Path path)
    {
        return path.ToString();
    }
    public static bool operator ==(Path left, Path right)
    {
        return left.Equals(right);
    }
    public static bool operator !=(Path left, Path right)
    {
        return !left.Equals(right);
    }
    public static Path operator +(Path left, Path right)
    {
        return left.Combine(right);
    }
    #endregion

    #region Converters

    partial class PathJsonConverter : JsonConverter<Path>
    {
        public override Path Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("");
            }

            var str = reader.GetString();

            if (str is null || str == string.Empty)
            {
                return Path.Empty;
            }

            return new Path(str.ToCharArray());
        }

        public override void Write(Utf8JsonWriter writer, Path value, JsonSerializerOptions options)
        {
            var str = value.ToString();

            writer.WriteStringValue(str);
        }
    }


    #endregion
}