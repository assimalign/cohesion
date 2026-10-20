using System;
using System.Buffers.Binary;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Protocol.Tests;

public sealed class DocumentWireCodecTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Completion has two big-endian signed counts")]
    public void Complete_QueryAndWrite_ShouldEncodeAffectedCount()
    {
        // Arrange / Act
        byte[] query = new DocumentProtocolCompleteMessage(2, -1).Encode();
        byte[] write = new DocumentProtocolCompleteMessage(0, 9).Encode();

        // Assert
        query.ShouldBe(new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 255, 255, 255, 255, 255, 255, 255, 255 });
        BinaryPrimitives.ReadInt64BigEndian(write.AsSpan(8)).ShouldBe(9);
        DocumentProtocolCompleteMessage.Decode(query).ShouldBe(new(2, -1));
        Should.Throw<ProtocolException>(() => new DocumentProtocolCompleteMessage(-1, 0).Encode());
        Should.Throw<ProtocolException>(() => new DocumentProtocolCompleteMessage(0, -2).Encode());
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Every new structured payload rejects truncation and trailing bytes")]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(69)]
    [InlineData(70)]
    [InlineData(71)]
    [InlineData(72)]
    public void Decode_InvalidBounds_ShouldReject(int type)
    {
        // Arrange
        byte[] payload = type switch
        {
            66 => new DocumentProtocolCompleteMessage(1, -1).Encode(),
            67 => new DocumentProtocolResultHeaderMessage([("東京", (byte)DatabaseType.String)], false).Encode(),
            69 => new DocumentProtocolCommandMessage(DocumentProtocolOperation.Put, "items", "one", new(ulong.MaxValue), "{}"u8.ToArray()).Encode(),
            70 => new DocumentProtocolStoredDocumentMessage(new("one", new(ulong.MaxValue), "[1]"u8.ToArray())).Encode(),
            71 => new DocumentProtocolCollectionMessage("items").Encode(),
            _ => new DocumentProtocolFailureMessage(ProtocolErrorCode.ParseFailure, "invalid", [new Diagnostic { Code = "COHDBL001", Message = "detail", Start = 1, End = 2, Severity = DiagnosticSeverity.Error }]).Encode(),
        };

        // Act / Assert
        Decode(type, payload);
        for (int length = 0; length < payload.Length; length++)
        {
            byte[] truncated = payload[..length];
            Should.Throw<ProtocolException>(() => Decode(type, truncated));
        }
        Should.Throw<ProtocolException>(() => Decode(type, [.. payload, 0]));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Commands preserve optional versions and Unicode identities")]
    [InlineData(DocumentProtocolOperation.Get)]
    [InlineData(DocumentProtocolOperation.Put)]
    [InlineData(DocumentProtocolOperation.Delete)]
    [InlineData(DocumentProtocolOperation.CreateCollection)]
    [InlineData(DocumentProtocolOperation.GetCollection)]
    [InlineData(DocumentProtocolOperation.DropCollection)]
    [InlineData(DocumentProtocolOperation.ListCollections)]
    public void Command_EachOperation_ShouldRoundTrip(DocumentProtocolOperation operation)
    {
        // Arrange
        bool document = operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete;
        var command = new DocumentProtocolCommandMessage(operation, operation == DocumentProtocolOperation.ListCollections ? "" : "東京",
            document ? "a/one" : "", operation is DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete ? new(ulong.MaxValue) : null,
            operation == DocumentProtocolOperation.Put ? "{\"a\":true}"u8.ToArray() : default);

        // Act
        var result = DocumentProtocolCommandMessage.Decode(command.Encode());

        // Assert
        result.Operation.ShouldBe(operation);
        result.Collection.ShouldBe(command.Collection);
        result.Id.ShouldBe(command.Id);
        result.ExpectedVersion.ShouldBe(command.ExpectedVersion);
        result.Content.ToArray().ShouldBe(command.Content.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Coded errors retain nullable diagnostic fields")]
    public void Failure_DiagnosticLocations_ShouldRoundTripWithoutParsingText()
    {
        // Arrange
        var diagnostic = new Diagnostic { Code = "COHDBL001", Message = "detail", Start = 5, Severity = DiagnosticSeverity.Error };
        var message = new DocumentProtocolFailureMessage(ProtocolErrorCode.ParseFailure, "summary", [diagnostic]);

        // Act
        var result = DocumentProtocolFailureMessage.Decode(message.Encode());

        // Assert
        var copied = result.Diagnostics.Single();
        (copied.Code, copied.Message, copied.Start, copied.End, copied.Line, copied.Severity, copied.Location)
            .ShouldBe((diagnostic.Code, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Severity, diagnostic.Location));
        Should.Throw<ProtocolException>(() => new DocumentProtocolFailureMessage(ProtocolErrorCode.ProtocolViolation, "fatal", []).Encode());
        Should.Throw<ProtocolException>(() => DocumentProtocolCommandMessage.Decode([255]));
    }

    private static object Decode(int type, byte[] payload) => type switch
    {
        66 => DocumentProtocolCompleteMessage.Decode(payload),
        67 => DocumentProtocolResultHeaderMessage.Decode(payload),
        69 => DocumentProtocolCommandMessage.Decode(payload),
        70 => DocumentProtocolStoredDocumentMessage.Decode(payload),
        71 => DocumentProtocolCollectionMessage.Decode(payload),
        _ => DocumentProtocolFailureMessage.Decode(payload),
    };
}
