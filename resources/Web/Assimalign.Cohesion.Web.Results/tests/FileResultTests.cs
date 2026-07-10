using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the three unconditional file built-ins: in-memory bytes, caller-supplied stream, and
/// physical path (with <c>Content-Type</c> inference via <c>HttpContentTypes.GetContentType</c>).
/// Range and precondition behavior is deliberately absent — deferred to #777.
/// </summary>
public class FileResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - File: writes bytes with octet-stream default and Content-Length")]
    public async Task ExecuteAsync_ByteFile_WritesBytesWithDefaults()
    {
        // Arrange
        TestHttpContext context = new();
        byte[] contents = Encoding.UTF8.GetBytes("file-bytes");

        // Act
        await Results.File(contents).ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("file-bytes");
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/octet-stream");
        context.Response.Headers[HttpHeaderKey.ContentLength].Value.ShouldBe("10");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - File: honors an explicit content type")]
    public async Task ExecuteAsync_ByteFileWithContentType_HonorsIt()
    {
        // Arrange
        TestHttpContext context = new();

        // Act
        await Results.File(new byte[] { 1, 2, 3 }, "image/png").ExecuteAsync(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("image/png");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - FileStream: copies a seekable stream with Content-Length and disposes it")]
    public async Task ExecuteAsync_SeekableStream_SetsLengthAndDisposes()
    {
        // Arrange
        TestHttpContext context = new();
        DisposeTrackingStream stream = new(Encoding.UTF8.GetBytes("streamed"));

        // Act
        await Results.FileStream(stream).ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("streamed");
        context.Response.Headers[HttpHeaderKey.ContentLength].Value.ShouldBe("8");
        stream.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - FileStream: a non-seekable stream never gets a Content-Length")]
    public async Task ExecuteAsync_NonSeekableStream_OmitsContentLength()
    {
        // Arrange
        TestHttpContext context = new();
        NonSeekableStream stream = new(Encoding.UTF8.GetBytes("chunked"));

        // Act
        await Results.FileStream(stream).ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("chunked");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PhysicalFile: infers the content type from the extension and sets the length")]
    public async Task ExecuteAsync_PhysicalFile_InfersContentTypeFromExtension()
    {
        // Arrange
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"web-results-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"ok\":true}");
        try
        {
            TestHttpContext context = new();

            // Act
            await Results.PhysicalFile(path).ExecuteAsync(context);

            // Assert
            context.ResponseBodyText().ShouldBe("{\"ok\":true}");
            context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/json");
            context.Response.Headers[HttpHeaderKey.ContentLength].Value.ShouldBe("11");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PhysicalFile: a missing file surfaces as FileNotFoundException, not a status code")]
    public async Task ExecuteAsync_MissingPhysicalFile_Throws()
    {
        // Arrange
        TestHttpContext context = new();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"web-results-missing-{Guid.NewGuid():N}.bin");

        // Act + Assert
        await Should.ThrowAsync<FileNotFoundException>(
            () => Results.PhysicalFile(path).ExecuteAsync(context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - File family: invalid factory arguments are rejected")]
    public void Factory_InvalidArguments_Throw()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.File(null!));
        Should.Throw<ArgumentNullException>(() => Results.FileStream(null!));
        Should.Throw<ArgumentException>(() => Results.PhysicalFile(""));
    }

    private sealed class DisposeTrackingStream : MemoryStream
    {
        public DisposeTrackingStream(byte[] contents) : base(contents)
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Wraps a buffer behind a forward-only read surface (CanSeek = false).</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] contents) => _inner = new MemoryStream(contents);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
