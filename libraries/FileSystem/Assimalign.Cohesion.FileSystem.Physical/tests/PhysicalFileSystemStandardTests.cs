using System;
using System.IO;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Runs the provider-agnostic <see cref="FileSystemStandardTests"/> suite against
/// <see cref="PhysicalFileSystem"/>. Each test gets a fresh temporary directory so the suite
/// runs in isolation from other tests and the host file system.
/// </summary>
public class PhysicalFileSystemStandardTests : FileSystemStandardTests, IDisposable
{
    private readonly string _root;

    public PhysicalFileSystemStandardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CohesionPhysicalStandard", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_root);
    }

    public override IFileSystem GetFileSystem()
    {
        var factory = new FileSystemFactoryBuilder()
            .AddPhysicalFileSystem(options =>
            {
                options.Root = _root;
            })
            .Build();

        return factory.Create("PhysicalFileSystem");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; test failures may leave files locked momentarily.
        }
    }
}
