using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>A mount whose lookup exception echoes sensitive request input.</summary>
internal sealed class FailingLookupFileSystem : IFileSystem
{
    public Size Size => default;
    public Size SpaceAvailable => default;
    public Size SpaceUsed => default;
    public string Name => "lookup-failure";
    public bool IsReadOnly => true;
    public IFileSystemDirectory RootDirectory => throw new NotSupportedException();
    public bool Exists(FileSystemPath path) => throw new FileSystemException("private-path.txt could not be opened");
    public IFileSystemInfo GetInfo(FileSystemPath path) => throw new NotSupportedException();
    public IFileSystemEventToken Watch(Glob? pattern) => throw new NotSupportedException();
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = default) => throw new NotSupportedException();
    public IFileSystemDirectory GetDirectory(FileSystemPath path) => throw new NotSupportedException();
    public IFileSystemFile GetFile(FileSystemPath path) => throw new NotSupportedException();
    public IFileSystemDirectory CreateDirectory(FileSystemPath path) => throw new NotSupportedException();
    public IFileSystemFile CreateFile(FileSystemPath path) => throw new NotSupportedException();
    public void DeleteDirectory(FileSystemPath path) => throw new NotSupportedException();
    public void DeleteFile(FileSystemPath path) => throw new NotSupportedException();
    public void CopyFile(FileSystemPath source, FileSystemPath destination) => throw new NotSupportedException();
    public void Move(FileSystemPath source, FileSystemPath destination) => throw new NotSupportedException();
    public IEnumerator<IFileSystemInfo> GetEnumerator() => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
