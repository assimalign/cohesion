using System;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

// Deviates from RootNamespace: stateless shared source retains its owning namespace.
namespace Assimalign.Cohesion.Database.Internal;

internal static class DatabaseRootDirectory
{
    internal const string RefusalCode = "CK06007";
    private const UnixFileMode ownerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode widerMode = UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly string[] _dataFileSets = ["sql", "keyvalue", "document", "graph", "blob"];

    internal static ReadOnlySpan<string> DataFileSets => _dataFileSets;

    internal static void Prepare(string path, Action<string, UnixFileMode, UnixFileMode> warn, string? tempDirectory = null)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(path)) { return; }
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var principal in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            {
                acl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            var root = new DirectoryInfo(path);
            if (root.Parent is { } parent) { Directory.CreateDirectory(parent.FullName); }
            FileSystemAclExtensions.Create(root, acl);
            return;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, ownerMode);
        }

        PrepareExistingUnix(path, File.GetUnixFileMode(path),
            () => FindForeignEntry(path, tempDirectory), mode => { if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, mode); } }, warn);
    }

    // Separate policy from the syscalls so a refused chmod can be exercised deterministically.
    internal static void PrepareExistingUnix(string path, UnixFileMode mode, Func<string?> findForeignEntry,
        Action<UnixFileMode> setMode, Action<string, UnixFileMode, UnixFileMode> warn)
    {
        if ((mode & widerMode) == 0) { return; }
        if (InspectUnixDirectory(path, path, findForeignEntry) is { } entry)
        {
            throw DatabaseException.CreateEngineFailure(RefusalCode,
                string.Format(CultureInfo.CurrentCulture, EngineErrorMessages.RootForeignEntry, path, Octal(mode), entry));
        }
        try
        {
            setMode(ownerMode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw DatabaseException.CreateEngineFailure(RefusalCode,
                string.Format(CultureInfo.CurrentCulture, EngineErrorMessages.RootModeChangeFailed, path, Octal(mode)), error);
        }
        warn(path, mode, ownerMode);
    }

    internal static string? InspectUnixDirectory(string rootPath, string path, Func<string?> inspect)
    {
        try { return inspect(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw DatabaseException.CreateEngineFailure(RefusalCode,
                string.Format(CultureInfo.CurrentCulture, EngineErrorMessages.RootInspectionFailed, path, rootPath), error);
        }
    }

    internal static string? FindForeignEntry(string path, string? tempDirectory)
    {
        string? tempPath = tempDirectory is null ? null : Path.GetFullPath(tempDirectory);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { return entry.FullName; }
            if ((entry.Attributes & FileAttributes.Directory) == 0)
            {
                if (entry.Name == ".DS_Store") { continue; }
                return entry.FullName;
            }
            if (entry.Name == ".cohesion-temp" || (tempPath is not null && ContainsTempDirectory(entry.FullName, tempPath))) { continue; }
            if (!DatabaseFileNames.IsStorageName(entry.Name)) { return entry.FullName; }
            if (InspectUnixDirectory(path, entry.FullName,
                () => IsEngineDataDirectory((DirectoryInfo)entry) ? null : entry.FullName) is { } foreign) { return foreign; }
        }
        return null;
    }

    private static bool ContainsTempDirectory(string entry, string tempPath)
    {
        // Match Sql option validation, including the platform's path case comparison.
        string relative = Path.GetRelativePath(entry, tempPath);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsEngineDataDirectory(DirectoryInfo directory)
    {
        foreach (string set in DataFileSets)
        {
            if (File.Exists(Path.Combine(directory.FullName, set + ".dat"))) { return true; }
        }
        // A create claims its directory before opening files; a drop removes data first.
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || !IsFileSetFile(entry.Name)) { return false; }
        }
        return true;
    }

    private static bool IsFileSetFile(string name)
    {
        if (name == "sql.create") { return true; }
        string extension = Path.GetExtension(name);
        if (extension is not (".dat" or ".log" or ".bak")) { return false; }
        string set = Path.GetFileNameWithoutExtension(name);
        return set == DatabaseFileNames.CatalogFileSet || DataFileSets.Contains(set);
    }

    internal static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');
}
