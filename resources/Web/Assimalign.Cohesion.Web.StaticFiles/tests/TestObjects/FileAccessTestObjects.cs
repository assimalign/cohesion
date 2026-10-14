using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// A response body that holds the first write until the test releases it, so a test can keep one
/// response in the middle of its copy — with its file open — while it drives another request.
/// </summary>
internal sealed class GatedResponseBody : Stream
{
    private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MemoryStream Written { get; } = new();

    public Task FirstWrite => _firstWrite.Task;

    public void Release() => _release.TrySetResult();

    public string ReadWritten() => Encoding.UTF8.GetString(Written.ToArray());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written.Length;

    public override long Position
    {
        get => Written.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _firstWrite.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Written.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// A file whose reported <see cref="Size"/> disagrees with its content: the window in which a file is
/// replaced, grown, or truncated between the moment its metadata is read and the moment it is opened.
/// </summary>
internal sealed class MisreportedSizeFile : IFileSystemFile
{
    private readonly byte[] _content;

    public MisreportedSizeFile(string name, string content, long reportedSize)
    {
        Name = name;
        _content = Encoding.UTF8.GetBytes(content);
        Size = reportedSize;
    }

    public Size Size { get; }
    public FileName Name { get; }
    public FileSystemPath Path => FileSystemPath.Parse("/" + Name);
    public DateTime CreatedOn { get; } = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime UpdatedOn { get; } = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime AccessedOn { get; } = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    public FileAttributes Attributes => FileAttributes.Normal;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileSystemDirectory Directory => throw new NotSupportedException();

    public Stream Open() => new MemoryStream(_content, writable: false);
    public Stream Open(FileMode fileMode) => Open();
    public Stream Open(FileMode fileMode, FileAccess fileAccess) => Open();
    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare) => Open();

    public IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare) => throw new NotSupportedException();
    public IFileSystemEventToken Watch() => throw new NotSupportedException();
    public void SetAttributes(FileAttributes attributes) => throw new NotSupportedException();
}
