using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystemGlobbing.Tests.TestUtility;

public class DisposableFileSystem : IDisposable
{
    public DisposableFileSystem()
    {
#if NET7_0_OR_GREATER
        DirectoryInfo = Directory.CreateTempSubdirectory();
#else
        DirectoryInfo = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        DirectoryInfo.Create();
#endif
        RootPath = DirectoryInfo.FullName;
    }

    public string RootPath { get; }

    public DirectoryInfo DirectoryInfo { get; }

    public DisposableFileSystem CreateFolder(string path)
    {
        Directory.CreateDirectory(Path.Combine(RootPath, path));
        return this;
    }

    public DisposableFileSystem CreateFile(string path)
    {
        File.WriteAllText(Path.Combine(RootPath, path), "temp");
        return this;
    }

    public DisposableFileSystem CreateFiles(params string[] fileRelativePaths)
    {
        foreach (var path in fileRelativePaths)
        {
            var fullPath = Path.Combine(RootPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            File.WriteAllText(
                fullPath,
                string.Format("Automatically generated for testing on {0:yyyy}/{0:MM}/{0:dd} {0:hh}:{0:mm}:{0:ss}", DateTime.UtcNow));
        }

        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, true);
        }
        catch
        {
            // Don't throw if this fails.
        }
    }
}
