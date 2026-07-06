using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// The default file-system-backed <see cref="IKeyRepository"/>: one file per key
/// (<c>&lt;keyId&gt;.key</c>) under a directory. Writes go to a temporary sibling and are then
/// atomically moved into place, so a concurrent reader never observes a partial document.
/// </summary>
/// <remarks>
/// Key documents contain master material in the clear in v1, so the directory's file-system
/// permissions are the confidentiality boundary. At-rest encryption of the documents is a
/// tracked follow-up. The store is safe for concurrent readers; the ring serializes its writes.
/// </remarks>
internal sealed class FileSystemKeyRepository : IKeyRepository
{
    private const string Extension = ".key";
    private const string TempExtension = ".tmp";

    private readonly string _directory;

    public FileSystemKeyRepository(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyDocument> GetAllKeys()
    {
        List<KeyDocument> documents = new();
        if (!Directory.Exists(_directory))
        {
            return documents;
        }

        foreach (string path in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            byte[] content;
            try
            {
                content = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // A file being replaced concurrently — skip it this pass.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            documents.Add(new KeyDocument(Path.GetFileNameWithoutExtension(path), content));
        }

        return documents;
    }

    /// <inheritdoc />
    public void StoreKey(KeyDocument key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateName(key.Name);
        Directory.CreateDirectory(_directory);

        string finalPath = Path.Combine(_directory, key.Name + Extension);
        string tempPath = finalPath + TempExtension;

        File.WriteAllBytes(tempPath, key.Content.ToArray());
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static void ValidateName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(name))
        {
            throw new DataProtectionException($"'{name}' is not a valid key document name.");
        }
    }
}
