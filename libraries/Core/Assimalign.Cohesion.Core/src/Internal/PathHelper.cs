using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Internal;

internal static class PathHelper
{
    private const char dot = '.';
    private static readonly char[] _separators = ['/', '\\'];
    private static readonly char[] _invalidFileChars = [.. Path.GetInvalidFileNameChars()];
    private static readonly char[] _invalidPathChars = [.. Path.GetInvalidPathChars(), '<', '>', '?' ];

    internal static bool IsDot(char value)
    {
        return value == dot;
    }
    internal static bool IsSeparator(char value)
    {
        return value == _separators[0] || value == _separators[1];
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

    internal static bool HasValidDriveLetter(string value)
    {
        if (value.Length >= 2)
        {
            if ((uint)((value[0] | 0x20) - 97) <= 25u && value[1] == ':')
            {
                return true;
            }
        }

        return false;
    }

    // Returns the starting and ending index of where to begin trimming a string
    internal static (int start, int end) GetTrimRange(string value, int startAt = 0)
    {
        int start = startAt;
        int end = value.Length - 1;

        CalculateTrimRange(value, ref start, ref end);

        return (start, end);
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

    internal static int GetTrimStart(string value)
    {
        int start = 0;

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

        return start;
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
