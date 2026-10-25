using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>A gateway state directory under the system temporary folder, removed when the test ends.</summary>
internal sealed class TemporaryTestDirectory : IDisposable
{
    public TemporaryTestDirectory()
    {
        Path = Directory.CreateTempSubdirectory("cohesion-gateway-").FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process the test started may still hold a file; the temporary folder is cleaned eventually.
        }
    }
}
