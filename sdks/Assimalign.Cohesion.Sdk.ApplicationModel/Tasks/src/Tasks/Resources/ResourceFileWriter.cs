using System;
using System.IO;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

// Deviates from the repo namespace-matches-assembly rule per design decision: this
// ApplicationModel-owned source is linked into Gateway and retains its owning namespace.
internal static class ResourceFileWriter
{
    public static void WriteIfChanged(string path, ReadOnlySpan<byte> content)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (File.Exists(fullPath))
        {
            byte[] existing = File.ReadAllBytes(fullPath);
            if (content.SequenceEqual(existing))
            {
                return;
            }
        }

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
