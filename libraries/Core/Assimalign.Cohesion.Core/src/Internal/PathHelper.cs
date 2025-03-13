using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Internal;

internal static class PathHelper
{
    private const char dot = '.';
    private static readonly char[] _separators = ['/', '\\'];
    private static readonly char[] _invalidFileChars = [
        '\"', '<', '>', '|', '\0',
            (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
            (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
            (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
            (char)31, ':', '*', '?', '\\', '/'
        ];
    private static readonly char[] _invalidPathChars = [  // DO NOT use Path.GetInvalidPathChars() - the results differ per platform.
        '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, 
        '<', '>', '?' 
        ];

    internal static bool IsDot(char value)
    {
        return value == dot;
    }
    internal static bool IsPathSeparator(char value)
    {
        return value == _separators[0] || value == _separators[1];
    }
    internal static bool IsValidDriveChar(char value)
    {
        return (uint)((value | 0x20) - 97) <= 25u;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsDirectorySeparator(char c)
    {
        if (c != '\\')
        {
            return c == '/';
        }
        return true;
    }
    internal static bool IsEffectivelyEmpty(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return true;
        }
        ReadOnlySpan<char> readOnlySpan = path;
        for (int i = 0; i < readOnlySpan.Length; i++)
        {
            if (readOnlySpan[i] != ' ')
            {
                return false;
            }
        }
        return true;
    }
    internal static string GetPathRoot(ReadOnlySpan<char> path)
    {
        if (IsEffectivelyEmpty(path))
        {
            return string.Empty;
        }
        int rootLength = GetRootLength(path);
        if (rootLength > 0)
        {
            return path.Slice(0, rootLength).ToString();
        }
        return  string.Empty;
    }
    internal static bool IsValidPathChar(char value)
    {
        for (int i = 0; i < _invalidPathChars.Length; i++)
        {
            if (_invalidPathChars[i] == value)
            {
                return false;
            }
        }

        return true;
    }
    internal static bool IsValidNameChar(char value)
    {
        for (int i = 0; i < _invalidFileChars.Length; i++)
        {
            if (_invalidFileChars[i] == value)
            {
                return false;
            }
        }

        return true;
    }
    internal static bool HasDriveLetter(string value)
    {
        if (value.Length >= 2)
        {
            if (IsValidDriveChar(value[0]) && value[1] == ':')
            {
                return true;
            }
        }

        return false;
    }
    internal static int GetRootLength(ReadOnlySpan<char> path)
    {
        int length = path.Length;
        int i = 0;
        bool flag = IsDevice(path);
        bool flag2 = flag && IsDeviceUNC(path);
        if ((!flag || flag2) && length > 0 && IsDirectorySeparator(path[0]))
        {
            if (flag2 || (length > 1 && IsDirectorySeparator(path[1])))
            {
                i = (flag2 ? 8 : 2);
                int num = 2;
                for (; i < length; i++)
                {
                    if (IsDirectorySeparator(path[i]) && --num <= 0)
                    {
                        break;
                    }
                }
            }
            else
            {
                i = 1;
            }
        }
        else if (flag)
        {
            for (i = 4; i < length && !IsDirectorySeparator(path[i]); i++)
            {
            }
            if (i < length && i > 4 && IsDirectorySeparator(path[i]))
            {
                i++;
            }
        }
        else if (length >= 2 && path[1] == ':' && IsValidDriveChar(path[0]))
        {
            i = 2;
            if (length > 2 && IsDirectorySeparator(path[2]))
            {
                i++;
            }
        }
        return i;
    }
    internal static bool IsDeviceUNC(ReadOnlySpan<char> path)
    {
        if (path.Length >= 8 && IsDevice(path) && IsDirectorySeparator(path[7]) && path[4] == 'U' && path[5] == 'N')
        {
            return path[6] == 'C';
        }
        return false;
    }
    internal static bool IsExtended(ReadOnlySpan<char> path)
    {
        if (path.Length >= 4 && path[0] == '\\' && (path[1] == '\\' || path[1] == '?') && path[2] == '?')
        {
            return path[3] == '\\';
        }
        return false;
    }
    internal static bool IsDevice(ReadOnlySpan<char> path)
    {
        if (!IsExtended(path))
        {
            if (path.Length >= 4 && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]) && (path[2] == '.' || path[2] == '?'))
            {
                return IsDirectorySeparator(path[3]);
            }
            return false;
        }
        return true;
    }
    internal static void CalculateTrimRange(string value, ref int start, ref int end)
    {
        // Calculate start of string
        for (; start < value.Length; start++)
        {
            int index = 0;
            char c = value[start];
            while (index < _separators.Length && _separators[index] != c)
            {
                index++;
            }
            if (index == _separators.Length) break;
        }

        // Calculate end of string
        for (; end >= start; end--)
        {
            int index = 0;
            char c = value[end];
            while (index < _separators.Length && _separators[index] != c)
            {
                index++;
            }
            if (index == _separators.Length) break;
        }
    }
    internal static void CalculateTrimStart(string value, ref int start)
    {
        // Calculate start of string
        for (; start < value.Length; start++)
        {
            int index = 0;
            char c = value[start];
            while (index < _separators.Length && _separators[index] != c)
            {
                index++;
            }
            if (index == _separators.Length) break;
        }
    }



    //    private static readonly char[] _invalidFileNameChars = Path.GetInvalidFileNameChars()
    //        .Where(c => c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar).ToArray();

    //    private static readonly char[] _invalidFilterChars = _invalidFileNameChars
    //        .Where(c => c != '*' && c != '|' && c != '?').ToArray();

    //    private static readonly char[] _pathSeparators = new[]
    //        {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar};

    //    internal static bool HasInvalidPathChars(string path)
    //    {
    //        return path.IndexOfAny(_invalidFileNameChars) != -1;
    //    }

    //    internal static bool HasInvalidFilterChars(string path)
    //    {
    //        return path.IndexOfAny(_invalidFilterChars) != -1;
    //    }

    //    internal static string EnsureTrailingSlash(string path)
    //    {
    //        if (!string.IsNullOrEmpty(path) &&
    //            path[path.Length - 1] != Path.DirectorySeparatorChar)
    //        {
    //            return path + Path.DirectorySeparatorChar;
    //        }

    //        return path;
    //    }

    //    internal static bool PathNavigatesAboveRoot(string path)
    //    {
    //        var tokenizer = new StringTokenizer(path, _pathSeparators);
    //        int depth = 0;

    //        foreach (StringSegment segment in tokenizer)
    //        {
    //            if (segment.Equals(".") || segment.Equals(""))
    //            {
    //                continue;
    //            }
    //            else if (segment.Equals(".."))
    //            {
    //                depth--;

    //                if (depth == -1)
    //                {
    //                    return true;
    //                }
    //            }
    //            else
    //            {
    //                depth++;
    //            }
    //        }

    //        return false;
    //    
}
