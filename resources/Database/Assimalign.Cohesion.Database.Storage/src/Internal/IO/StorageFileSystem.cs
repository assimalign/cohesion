using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.FileSystem;

/// <summary>Opens storage assets without coupling durability to a stream type.</summary>
internal static class StorageFileSystem
{
    internal static IFileSystemFileHandle OpenHandle(string path, IFileSystem? fileSystem, FileShare share,
        FileMode mode = FileMode.OpenOrCreate)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (mode is not (FileMode.Open or FileMode.OpenOrCreate or FileMode.Create or FileMode.CreateNew))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, ErrorMessages.StorageFileSystemOpenHandleFailure);
        }
        if (fileSystem is null)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)!;
            // Preserve the default file opener's missing-parent failure; provider creation
            // otherwise creates its parent chain as part of CreateFile.
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageFileSystemOpenHandleFailure2, fullPath));
            }
            // Resolve host paths before handing a provider-relative name to the physical file system.
            using var physical = new PhysicalFileSystem(directory);
            return OpenHandle(Path.GetFileName(fullPath), physical, share, mode);
        }

        IFileSystemFile file;
        if (fileSystem.Exists(path))
        {
            if (mode == FileMode.CreateNew)
            {
                throw new IOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageFileSystemOpenHandleIO, path));
            }
            file = fileSystem.GetFile(path);
        }
        else
        {
            if (mode == FileMode.Open)
            {
                throw new FileNotFoundException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FileSystemOpenHandleFileNotFound, path), path);
            }
            try
            {
                file = fileSystem.CreateFile(path);
            }
            catch (FileSystemException exception) when (exception.Code == FileSystemErrorCode.Conflict && mode != FileMode.CreateNew)
            {
                // Reuse the entry when the provider reports a concurrent creation conflict.
                file = fileSystem.GetFile(path);
            }
        }

        // File objects belong to the provider; storage owns only the returned handle.
        // CreateFile already materialized a newly created entry. Open it without
        // truncation for CreateNew; Create retains its explicit truncation policy.
        return file.OpenHandle(mode == FileMode.CreateNew ? FileMode.Open : mode, FileAccess.ReadWrite, share);
    }
}
