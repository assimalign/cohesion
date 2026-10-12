using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

internal sealed class LongPathTestDirectory : IDisposable
{
    public LongPathTestDirectory()
    {
        Root = Directory.CreateTempSubdirectory("cohesion-long-path-").FullName;
        HostDirectory = Root;
        while (HostDirectory.Length < 280)
        {
            HostDirectory = Path.Combine(HostDirectory, "resource content with spaces " + new string('x', 40));
        }
    }

    public string Root { get; }

    public string HostDirectory { get; }

    public string ExecutablePath => Path.Combine(
        HostDirectory, "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.exe");

    public void CreateExecutable(bool copyHost)
    {
        Directory.CreateDirectory(HostDirectory);
        if (copyHost)
        {
            foreach (string source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                File.Copy(source, Path.Combine(HostDirectory, Path.GetFileName(source)));
            }
        }
        else
        {
            File.WriteAllText(ExecutablePath, "Long-path file-system capability check.");
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
