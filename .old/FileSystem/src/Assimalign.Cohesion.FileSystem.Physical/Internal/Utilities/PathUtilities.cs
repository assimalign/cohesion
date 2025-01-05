using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;


internal static class PathUtilities
{
    private static readonly char[] _invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars()
        .Where(c => c != System.IO.Path.DirectorySeparatorChar && c != System.IO.Path.AltDirectorySeparatorChar).ToArray();

    private static readonly char[] _invalidFilterChars = _invalidFileNameChars
        .Where(c => c != '*' && c != '|' && c != '?').ToArray();

    private static readonly char[] _pathSeparators = new[]
        {System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar};

    internal static bool HasInvalidPathChars(string path)
    {
        return path.IndexOfAny(_invalidFileNameChars) != -1;
    }

    internal static bool HasInvalidFilterChars(string path)
    {
        return path.IndexOfAny(_invalidFilterChars) != -1;
    }

    internal static string EnsureTrailingSlash(string path)
    {
        if (!string.IsNullOrEmpty(path) &&
            path[path.Length - 1] != System.IO.Path.DirectorySeparatorChar)
        {
            return path + System.IO.Path.DirectorySeparatorChar;
        }

        return path;
    }

    internal static bool PathNavigatesAboveRoot(string path)
    {
        int depth = 0;

        foreach (var segment in GetSegments(path))
        {
            if (segment.Equals(".") || segment.Equals(""))
            {
                continue;
            }
            else if (segment.Equals(".."))
            {
                depth--;

                if (depth == -1)
                {
                    return true;
                }
            }
            else
            {
                depth++;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetSegments(string path)
    {
        int index = 0;

        for (int i = 0; i < path.Length; i++)
        {
            if (_pathSeparators.Contains(path[i]))
            {
                yield return path.Substring(index, i - 1);

                index = i + 1;
            }
            if ((i + 1) == path.Length)
            {
                yield return path.Substring(index, i + 1);
            }
        }
    }
}
