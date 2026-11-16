using System;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

using Assimalign.Cohesion.Database.Types;

// Deviates from RootNamespace: stateless shared source retains its owning namespace.
namespace Assimalign.Cohesion.Database.Internal;

internal static class DatabaseRootDirectory
{
    internal const string RefusalCode = "CK06007";
    private const UnixFileMode ownerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode allowedMode = ownerMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    internal static void Prepare(string path, string dataFileSet, Action<string, UnixFileMode, UnixFileMode> warn, string? tempDirectory = null)
    {
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
            FileSystemAclExtensions.Create(new DirectoryInfo(path), acl);
            return;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, ownerMode);
        }

        PrepareExistingUnix(path, File.GetUnixFileMode(path),
            () => IsOwned(path, dataFileSet, tempDirectory), mode => { if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, mode); } }, warn);
    }

    // Separate policy from the syscalls so a refused chmod can be exercised deterministically.
    internal static void PrepareExistingUnix(string path, UnixFileMode mode, Func<bool> isOwned,
        Action<UnixFileMode> setMode, Action<string, UnixFileMode, UnixFileMode> warn)
    {
        if ((mode & ~allowedMode) == 0) { return; }
        try
        {
            if (!isOwned()) { throw Refuse(path, mode); }
            setMode(ownerMode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Refuse(path, mode, error);
        }
        warn(path, mode, ownerMode);
    }

    internal static bool IsOwned(string path, string dataFileSet, string? tempDirectory)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            { return false; }
            if (tempDirectory is not null && (string.Equals(entry.FullName, Path.GetFullPath(tempDirectory), StringComparison.Ordinal)
                || Path.GetFullPath(tempDirectory).StartsWith(entry.FullName + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            { continue; }
            if (!DatabaseFileNames.IsStorageName(entry.Name) || !File.Exists(Path.Combine(entry.FullName, dataFileSet + ".dat")))
            { return false; }
        }
        return true;
    }

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');

    private static DatabaseException Refuse(string path, UnixFileMode mode, Exception? cause = null)
    {
        string message = string.Format(CultureInfo.CurrentCulture, EngineErrorMessages.RootPermissionsInvalid, path, Octal(mode));
        return new DatabaseException(message,
            [new Diagnostic { Code = RefusalCode, Message = message, Severity = DiagnosticSeverity.Error }], cause);
    }
}
