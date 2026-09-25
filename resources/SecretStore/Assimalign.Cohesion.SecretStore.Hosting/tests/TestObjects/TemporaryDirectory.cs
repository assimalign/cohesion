using System;
using System.IO;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = SecretStoreTestHost.CreateTemporaryDirectory();
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
