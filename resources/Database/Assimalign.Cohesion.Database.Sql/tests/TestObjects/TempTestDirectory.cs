using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class TempTestDirectory
{
    public static void Create(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
}
