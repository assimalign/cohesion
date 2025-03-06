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
[DebuggerDisplay("{Value}")]
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
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public FileSystemPath(string path)
    {
        ThrowHelper.ThrowIfNullOrEmpty(path, nameof(path));

        if (System.IO.Path.GetInvalidPathChars().Intersect(path).Any())
        {
            ThrowHelper.ThrowArgumentException($"path contains illegal characters.");
        }

        int end = path.Length - 1;
        int start = 0;
        char[] trimChars = ['/', '\\'];

        for (start = 0; start < path.Length; start++)
        {
            int index = 0;
            char c = path[start];
            while (index < trimChars.Length && trimChars[index] != c)
            {
                index++;
            }
            if (index == trimChars.Length) break;
        }
        for (end = path.Length - 1; end >= start; end--)
        {
            int index = 0;
            char c = path[end];
            while (index < trimChars.Length && trimChars[index] != c)
            {
                index++;
            }
            if (index == trimChars.Length) break;
        }

        int resize = 0;
        char previous = default;

        var value = string.Create((end + 1) - start, path, (span, value) =>
        {
            // Let's convert all backward slashes to forward slashes
            for (int i = start; i < (end + 1); i++)
            {
                var current = value[i];

                if (current == '\\') current = '/';
                // Check for excessive slashes
                if (previous == '/' && current == '/')
                {
                    resize++;
                    continue;
                }

                previous = current;
                span[i - start - resize] = current;
            }
        });

        if (resize > 0)
        {
            Value = value.Remove(value.Length - resize);
        }
        else
        {
            Value = value;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public char this[int index] => Value[index];

    /// <summary>
    /// The raw string path.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The length of the path.
    /// </summary>
    public int Length => Value.Length;

    /// <summary>
    /// An empty path.
    /// </summary>
    public static FileSystemPath Empty => "/";

    /// <summary>
    /// The directory separator.
    /// </summary>
    public const char Separator = '/';

    /// <summary>
    /// Returns the segments of the path.
    /// </summary>
    /// <returns></returns>
    public string[] GetSegments()
    {
        return Value.Split(Separator);
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
        return IO.Path.Combine([.. paths]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public bool EndsWith(string value)
    {
        return Value.EndsWith(value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool EndsWith(string value, StringComparison comparison)
    {
        return Value.EndsWith(value, comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public bool StartsWith(string value)
    {
        return Value.StartsWith(value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(string value, StringComparison comparison)
    {
        return Value.StartsWith(value, comparison);
    }

    #region Overloads

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }
        if (obj is FileSystemPath path)
        {
            return Equals(path);
        }
        return false;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int code = Value.GetHashCode();

        return (int)((uint)code | ((uint)code << 16));
    }

    #endregion

    #region Interfaces

    public bool Equals(FileSystemPath other)
    {
        return Equals(this, other, StringComparison.Ordinal);
    }

    public bool Equals(FileSystemPath other, StringComparison comparison)
    {
        return Equals(this, other, comparison);
    }

    public bool Equals(FileSystemPath left, FileSystemPath right)
    {
        return Equals(left, right, StringComparison.Ordinal);
    }

    public int GetHashCode([DisallowNull] FileSystemPath obj)
    {
        return obj.GetHashCode();
    }

    public int CompareTo(FileSystemPath other)
    {
        return CompareTo(this, other, StringComparison.Ordinal);
    }

    public int CompareTo(FileSystemPath other, StringComparison comparison)
    {
        return CompareTo(this, other, comparison);
    }

    public static bool Equals(FileSystemPath left, FileSystemPath right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(left.Value, right.Value);
    }

    public static int CompareTo(FileSystemPath left, FileSystemPath right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Compare(left.Value, right.Value);
    }

    #endregion

    #region Operators

    public static implicit operator FileSystemPath(string path)
    {
        return new FileSystemPath(path);
    }

    public static implicit operator string(FileSystemPath path)
    {
        return path.Value;
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