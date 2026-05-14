using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Path-routing helpers for <see cref="AggregateFileSystem"/>. The router uses a longest-prefix
/// match — given the mount table {"/data", "/data/cache"} and the request path "/data/cache/x",
/// it returns the "/data/cache" mount because that is the longest mount whose path is a prefix
/// of the request.
/// </summary>
internal static class AggregateRouter
{
    /// <summary>
    /// Sorts <paramref name="mounts"/> in descending order of <see cref="AggregateMount.MountPath"/>
    /// length so <see cref="Resolve"/> can scan once and return the first hit. The aggregate
    /// caches the sorted view; the router never mutates the input list.
    /// </summary>
    public static List<AggregateMount> SortByLongestPrefix(IEnumerable<AggregateMount> mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);

        var sorted = new List<AggregateMount>(mounts);
        sorted.Sort((left, right) =>
            right.MountPath.ToString().Length.CompareTo(left.MountPath.ToString().Length));
        return sorted;
    }

    /// <summary>
    /// Finds the mount that owns <paramref name="absolutePath"/>. Returns <see langword="null"/>
    /// when no mount applies — that means the path falls in the aggregate's synthetic-root
    /// territory and the caller must decide how to handle it.
    /// </summary>
    /// <param name="sortedMounts">Mount table sorted by <see cref="SortByLongestPrefix"/>.</param>
    /// <param name="absolutePath">Aggregate-side absolute path.</param>
    public static AggregateMount? Resolve(IReadOnlyList<AggregateMount> sortedMounts, FileSystemPath absolutePath)
    {
        ArgumentNullException.ThrowIfNull(sortedMounts);

        string text = NormalizeAbsolute(absolutePath);

        foreach (var mount in sortedMounts)
        {
            string mountText = mount.MountPath.ToString();

            // "/" matches everything.
            if (mountText == "/")
            {
                return mount;
            }

            if (text.Equals(mountText, StringComparison.Ordinal))
            {
                return mount;
            }

            // The mount path must be followed by a separator for it to be a prefix — otherwise
            // "/data" would erroneously claim "/database".
            if (text.Length > mountText.Length
                && text.StartsWith(mountText, StringComparison.Ordinal)
                && text[mountText.Length] == '/')
            {
                return mount;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects whether <paramref name="absolutePath"/> is a synthetic intermediate directory — a
    /// prefix on the way to a mounted provider but not itself a mount root. For example, with
    /// only "/data/cache" mounted, "/data" is synthetic.
    /// </summary>
    public static bool IsSyntheticIntermediate(IReadOnlyList<AggregateMount> sortedMounts, FileSystemPath absolutePath)
    {
        ArgumentNullException.ThrowIfNull(sortedMounts);

        string text = NormalizeAbsolute(absolutePath);

        if (text == "/")
        {
            // Root is synthetic unless something is mounted at "/".
            foreach (var mount in sortedMounts)
            {
                if (mount.MountPath.ToString() == "/")
                {
                    return false;
                }
            }
            return sortedMounts.Count > 0;
        }

        string prefix = text + "/";
        foreach (var mount in sortedMounts)
        {
            string mountText = mount.MountPath.ToString();
            if (mountText.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the first-segment children that should appear under <paramref name="absolutePath"/>
    /// when treating it as a synthetic directory. Used by <see cref="IFileSystemDirectory.GetDirectories"/>
    /// on the synthetic root and its intermediate ancestors.
    /// </summary>
    public static HashSet<string> SyntheticChildren(IReadOnlyList<AggregateMount> sortedMounts, FileSystemPath absolutePath)
    {
        ArgumentNullException.ThrowIfNull(sortedMounts);

        string text = NormalizeAbsolute(absolutePath);
        string prefix = text == "/" ? "/" : text + "/";

        var children = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in sortedMounts)
        {
            string mountText = mount.MountPath.ToString();
            if (!mountText.StartsWith(prefix, StringComparison.Ordinal) || mountText.Length == prefix.Length)
            {
                continue;
            }

            // The first segment after the prefix is the synthetic child name.
            int end = mountText.IndexOf('/', prefix.Length);
            string segment = end < 0
                ? mountText.Substring(prefix.Length)
                : mountText.Substring(prefix.Length, end - prefix.Length);

            if (!string.IsNullOrEmpty(segment))
            {
                children.Add(segment);
            }
        }

        return children;
    }

    /// <summary>
    /// Returns the canonical form ("/foo/bar", no trailing slash, leading slash present) used
    /// by every comparison in this file.
    /// </summary>
    private static string NormalizeAbsolute(FileSystemPath path)
    {
        string text = path.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return "/";
        }
        if (text[0] != '/')
        {
            text = "/" + text;
        }
        if (text.Length > 1 && text[text.Length - 1] == '/')
        {
            text = text.Substring(0, text.Length - 1);
        }
        return text;
    }
}
