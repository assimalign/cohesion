using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class DuplexPipeStreamTests
{
    [Fact]
    public async Task ReadAsync_WhenBytesAreBuffered_ShouldCopyBytesAndAdvanceReader()
    {
        // Arrange
        Pipe input = new(new PipeOptions(useSynchronizationContext: false));
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));
        DuplexPipeStream stream = new(input.Reader, output.Writer);
        byte[] payload = [1, 2, 3, 4, 5];
        await input.Writer.WriteAsync(payload);
        byte[] destination = new byte[16];

        // Act
        int read = await stream.ReadAsync(destination);

        // Assert
        read.ShouldBe(payload.Length);
        destination.Take(read).ShouldBe(payload);

        // A subsequent read yields only newly written bytes, proving the reader advanced.
        await input.Writer.WriteAsync(new byte[] { 9 });
        int second = await stream.ReadAsync(destination);
        second.ShouldBe(1);
        destination[0].ShouldBe((byte)9);
    }

    [Fact]
    public async Task ReadAsync_AfterWriterCompletes_ShouldReturnZero()
    {
        // Arrange
        Pipe input = new(new PipeOptions(useSynchronizationContext: false));
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));
        DuplexPipeStream stream = new(input.Reader, output.Writer);
        input.Writer.Complete();

        // Act
        int read = await stream.ReadAsync(new byte[8]);

        // Assert
        read.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAsync_WhenDestinationSmallerThanAvailable_ShouldReturnPartialReads()
    {
        // Arrange
        Pipe input = new(new PipeOptions(useSynchronizationContext: false));
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));
        DuplexPipeStream stream = new(input.Reader, output.Writer);
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        await input.Writer.WriteAsync(payload);
        byte[] destination = new byte[3];

        // Act
        int first = await stream.ReadAsync(destination);
        byte[] firstBytes = destination.Take(first).ToArray();
        int second = await stream.ReadAsync(destination);
        byte[] secondBytes = destination.Take(second).ToArray();
        int third = await stream.ReadAsync(destination);
        byte[] thirdBytes = destination.Take(third).ToArray();

        // Assert
        first.ShouldBe(3);
        firstBytes.ShouldBe(new byte[] { 1, 2, 3 });
        second.ShouldBe(3);
        secondBytes.ShouldBe(new byte[] { 4, 5, 6 });
        third.ShouldBe(2);
        thirdBytes.ShouldBe(new byte[] { 7, 8 });
    }

    [Fact]
    public async Task WriteAsync_ThenFlushAsync_ShouldDeliverBytesToFarReader()
    {
        // Arrange
        Pipe input = new(new PipeOptions(useSynchronizationContext: false));
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));
        DuplexPipeStream stream = new(input.Reader, output.Writer);
        byte[] payload = Encoding.UTF8.GetBytes("hello far reader");

        // Act
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        // Assert
        ReadResult result = await output.Reader.ReadAsync();
        result.Buffer.ToArray().ShouldBe(payload);
        output.Reader.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task AsStream_OnConnection_ShouldReturnWorkingStreamOverConnectionPipes()
    {
        // Arrange
        TestConnection connection = new();
        byte[] inbound = Encoding.UTF8.GetBytes("from peer");
        byte[] outbound = Encoding.UTF8.GetBytes("to peer");

        // Act
        Stream stream = connection.AsStream();

        // Assert
        stream.ShouldNotBeNull();

        // Bytes the peer sends arrive through the stream's read side.
        await connection.PeerOutput.WriteAsync(inbound);
        byte[] readBuffer = new byte[inbound.Length];
        await stream.ReadExactlyAsync(readBuffer);
        readBuffer.ShouldBe(inbound);

        // Bytes written to the stream arrive at the peer's reader.
        await stream.WriteAsync(outbound);
        await stream.FlushAsync();
        ReadResult peerRead = await connection.PeerInput.ReadAsync();
        peerRead.Buffer.ToArray().ShouldBe(outbound);
        connection.PeerInput.AdvanceTo(peerRead.Buffer.End);
    }

    [Fact]
    public void Ctor_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        Pipe pipe = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new DuplexPipeStream((IDuplexPipe)null!));
        Should.Throw<ArgumentNullException>(() => new DuplexPipeStream(null!, pipe.Writer));
        Should.Throw<ArgumentNullException>(() => new DuplexPipeStream(pipe.Reader, null!));
    }

    [Fact]
    public void Seek_OnDuplexPipeStream_ShouldThrowNotSupportedException()
    {
        // Arrange
        Pipe pipe = new();
        DuplexPipeStream stream = new(pipe.Reader, pipe.Writer);

        // Act & Assert
        stream.CanRead.ShouldBeTrue();
        stream.CanWrite.ShouldBeTrue();
        stream.CanSeek.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.SetLength(1));
        Should.Throw<NotSupportedException>(() => _ = stream.Length);
        Should.Throw<NotSupportedException>(() => _ = stream.Position);
    }
}
