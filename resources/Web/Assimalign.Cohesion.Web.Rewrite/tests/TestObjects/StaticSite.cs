using System.IO;
using System.Text;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

/// <summary>
/// Builds populated <see cref="InMemoryFileSystem"/> mounts for the static-file scenarios.
/// </summary>
internal static class StaticSite
{
    public static InMemoryFileSystem Create(params (string Path, string Content)[] files)
    {
        InMemoryFileSystem fileSystem = new(new InMemoryFileSystemOptions
        {
            Name = "rewrite-site",
        });

        foreach ((string path, string content) in files)
        {
            IFileSystemFile file = fileSystem.CreateFile(path);
            using Stream stream = file.Open(FileMode.Open, FileAccess.Write);
            byte[] payload = Encoding.UTF8.GetBytes(content);
            stream.Write(payload, 0, payload.Length);
        }

        return fileSystem;
    }
}
