using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class ClientFrameWriterTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Blob chunk acknowledgements do not identify Documents commands (#1725)")]
    public async Task WriteFrameAsync_BlobAcknowledgement_ShouldNotRequireTransactionStatus()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, BlobProtocol.Family, leaveOpen: true);
        var writer = new ClientFrameWriter(channel.Writer, BlobProtocol.Family);

        // Act
        writer.BeginExchange();
        await writer.WriteFrameAsync(new((ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement, new byte[8]), CancellationToken.None);

        // Assert
        writer.RequiresTransactionStatus.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Documents commands still require transaction status (#1725)")]
    public async Task WriteFrameAsync_DocumentsCommand_ShouldRequireTransactionStatus()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, DocumentProtocol.Family, leaveOpen: true);
        var writer = new ClientFrameWriter(channel.Writer, DocumentProtocol.Family);

        // Act
        writer.BeginExchange();
        await writer.WriteFrameAsync(new((ProtocolMessageType)69, new byte[] { 7 }), CancellationToken.None);

        // Assert
        writer.RequiresTransactionStatus.ShouldBeTrue();
    }
}
