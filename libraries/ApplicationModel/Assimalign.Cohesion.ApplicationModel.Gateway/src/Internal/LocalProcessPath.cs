using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal static partial class LocalProcessPath
{
    private const int maxPath = 260;

    public static string ResolveExecutable(string path, Func<string, string?> shortPathResolver)
    {
        string fullPath = Path.GetFullPath(Unquote(path));
        return OperatingSystem.IsWindows()
            ? ResolveWindowsPath(fullPath, "executable", shortPathResolver)
            : fullPath;
    }

    public static string Unquote(string path) => OperatingSystem.IsWindows()
        && path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1]
            : path;

    public static string ResolveCommand(string command, Func<string, string?> shortPathResolver)
    {
        if (!OperatingSystem.IsWindows())
        {
            return command;
        }

        string path = Unquote(command);
        if (!Path.IsPathRooted(path)
            && !path.Contains(Path.DirectorySeparatorChar)
            && !path.Contains(Path.AltDirectorySeparatorChar))
        {
            return command;
        }

        string fullPath = Path.GetFullPath(path);
        // Leave short commands to Process.Start, including its quoting and search rules.
        return RemoveExtendedPrefix(fullPath).Length < maxPath
            ? command
            : ResolveWindowsPath(fullPath, "executable", shortPathResolver);
    }

    public static string ResolveWorkingDirectory(string path, Func<string, string?> shortPathResolver)
    {
        path = Unquote(path);
        if (!OperatingSystem.IsWindows() || path.Length == 0)
        {
            return path;
        }

        string fullPath = Path.GetFullPath(path);
        return RemoveExtendedPrefix(fullPath).Length < maxPath
            ? path
            : ResolveWindowsPath(fullPath, "working directory", shortPathResolver);
    }

    internal static string ResolveWindowsPath(
        string fullPath,
        string purpose,
        Func<string, string?> shortPathResolver)
    {
        fullPath = RemoveExtendedPrefix(fullPath);
        if (fullPath.Length < maxPath)
        {
            return fullPath;
        }

        string? shortPath = shortPathResolver(fullPath);
        if (!string.IsNullOrEmpty(shortPath))
        {
            shortPath = RemoveExtendedPrefix(shortPath);
            if (shortPath.Length < maxPath)
            {
                return shortPath;
            }
        }

        throw new InvalidOperationException(
            $"COHLOCAL001: LocalGateway cannot use {purpose} '{fullPath}': its full path is " +
            $"{fullPath.Length} characters (Windows requires fewer than {maxPath} for this launch), " +
            "and no usable 8.3 short path is available. Move the application to a shorter path, " +
            "or enable 8.3 names on the volume and recreate the application's directories and files. " +
            "LongPathsEnabled and the extended-length path prefix do not remove this CreateProcess limit.");
    }

    public static unsafe string? TryGetShortPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        fullPath = RemoveExtendedPrefix(fullPath);
        string extendedPath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;

        // A usable launch path has fewer than MAX_PATH characters. Allow room for the
        // extended UNC prefix too; a larger required buffer cannot yield a usable result.
        const int bufferLength = maxPath + 8;
        char* buffer = stackalloc char[bufferLength];
        uint length = GetShortPathName(extendedPath, buffer, bufferLength);
        return length == 0 || length >= bufferLength
            ? null
            : RemoveExtendedPrefix(new string(buffer, 0, (int)length));
    }

    private static string RemoveExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetShortPathName(string longPath, char* shortPath, uint bufferLength);
}
