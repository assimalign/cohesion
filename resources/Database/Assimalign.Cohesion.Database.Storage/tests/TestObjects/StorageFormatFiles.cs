using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Rewrites the storage format version page 0 of a closed data file names, for the format-refusal
/// tests of the model engines (#1251). Linked into each engine's test project.
/// </summary>
internal static class StorageFormatFiles
{
    /// <summary>
    /// Writes <paramref name="version"/> over the format version of the data file at
    /// <paramref name="path"/>, at the offset every storage format keeps it: the identity block
    /// follows the page header, and the version follows the magic number.
    /// </summary>
    /// <param name="path">A data file (<c>.dat</c>) of a closed file set.</param>
    /// <param name="version">The storage format version page 0 is to name.</param>
    /// <exception cref="InvalidOperationException">The file is not a storage data file.</exception>
    internal static void WriteVersion(string path, int version)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < Page.Size || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Page.HeaderSize)) != StorageFileHeader.ExpectedMagic)
        {
            throw new InvalidOperationException($"'{path}' is not a storage data file.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(Page.HeaderSize + sizeof(int)), version);
        File.WriteAllBytes(path, bytes);
    }
}
