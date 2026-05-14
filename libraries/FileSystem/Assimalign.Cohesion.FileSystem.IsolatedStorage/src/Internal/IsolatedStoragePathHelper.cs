using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Translates between <see cref="FileSystemPath"/> (which uses '/' separators and an implicit
/// root prefix) and the string paths consumed by <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/>
/// (which expects relative paths without a leading separator).
/// </summary>
internal static class IsolatedStoragePathHelper
{
    /// <summary>
    /// The implicit root prefix for an <see cref="IFileSystem"/> backed by isolated storage. The
    /// store itself does not have an addressable root, but the contract still requires a stable
    /// <see cref="FileSystemPath"/> for the root directory.
    /// </summary>
    public const string Root = "/";

    /// <summary>
    /// Normalizes <paramref name="path"/> into an absolute <see cref="FileSystemPath"/> rooted at
    /// "/" by merging it onto the root. Relative paths and paths that already start with "/" both
    /// produce the same canonical absolute form.
    /// </summary>
    public static FileSystemPath ToAbsolute(FileSystemPath path)
    {
        FileSystemPath root = Root;

        if (path.IsEmpty)
        {
            return root;
        }

        return root.Merge(path);
    }

    /// <summary>
    /// Converts an absolute <see cref="FileSystemPath"/> into the relative store-side string
    /// understood by <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/>. Strips the
    /// leading separator and any trailing separator; the root becomes the empty string.
    /// </summary>
    public static string ToStorePath(FileSystemPath path)
    {
        string text = path.ToString();

        if (string.IsNullOrEmpty(text) || text == Root)
        {
            return string.Empty;
        }

        if (text[0] == '/')
        {
            text = text.Substring(1);
        }

        // Strip any trailing separator so callers can append a relative pattern without
        // producing double slashes (e.g. when building "dir/*" search patterns).
        if (text.Length > 0 && text[text.Length - 1] == '/')
        {
            text = text.Substring(0, text.Length - 1);
        }

        return text;
    }

    /// <summary>
    /// Converts a store-side relative path back to an absolute <see cref="FileSystemPath"/>
    /// rooted at "/". The empty string maps to the root.
    /// </summary>
    public static FileSystemPath FromStorePath(string storePath)
    {
        if (string.IsNullOrEmpty(storePath))
        {
            return Root;
        }

        // IsolatedStorage may surface OS-style separators on Windows; normalize to '/'.
        if (storePath.IndexOf('\\') >= 0)
        {
            storePath = storePath.Replace('\\', '/');
        }

        if (storePath[0] != '/')
        {
            storePath = "/" + storePath;
        }

        return FileSystemPath.Parse(storePath);
    }

    /// <summary>
    /// Joins <paramref name="parent"/> with a relative <paramref name="child"/> segment and
    /// returns the absolute <see cref="FileSystemPath"/> rooted at "/".
    /// </summary>
    public static FileSystemPath Join(FileSystemPath parent, string child)
    {
        ArgumentException.ThrowIfNullOrEmpty(child);

        FileSystemPath parentAbs = ToAbsolute(parent);
        return parentAbs.Join(child);
    }

    /// <summary>
    /// Builds the search-pattern argument passed to <see cref="System.IO.IsolatedStorage.IsolatedStorageFile.GetFileNames(string)"/>
    /// and <see cref="System.IO.IsolatedStorage.IsolatedStorageFile.GetDirectoryNames(string)"/>
    /// when listing entries inside <paramref name="directory"/>.
    /// </summary>
    public static string ChildSearchPattern(FileSystemPath directory)
    {
        string store = ToStorePath(directory);
        return string.IsNullOrEmpty(store) ? "*" : store + "/*";
    }
}
