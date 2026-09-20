using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public class ProtocolMessageTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Protocol: model payloads preserve legacy encoding")]
    public void Messages_EncodeDecode_ShouldRoundTrip()
    {
        ((byte)KeyValueProtocolMessageType.Execute).ShouldBe((byte)5);
        ((byte)KeyValueProtocolMessageType.ResultHeader).ShouldBe((byte)6);
        ((byte)KeyValueProtocolMessageType.ResultRow).ShouldBe((byte)7);
        ((byte)KeyValueProtocolMessageType.ResultComplete).ShouldBe((byte)8);
        ((byte)KeyValueProtocolMessageType.Transaction).ShouldBe((byte)9);

        var execute = new ProtocolExecuteMessage("SELECT * FROM users WHERE id = @id;", new Dictionary<string, byte[]>
        {
            ["id"] = new byte[] { 0x05, 0x01, 0x02 },
        });
        var decodedExecute = ProtocolExecuteMessage.Decode(execute.Encode());
        decodedExecute.Statement.ShouldBe(execute.Statement);
        decodedExecute.Parameters["id"].ShouldBe(new byte[] { 0x05, 0x01, 0x02 });

        var headerMessage = new ProtocolResultHeaderMessage(new List<(string, byte)> { ("id", 5), ("name", 9) });
        var decodedHeader = ProtocolResultHeaderMessage.Decode(headerMessage.Encode());
        decodedHeader.Columns.Count.ShouldBe(2);
        decodedHeader.Columns[1].Name.ShouldBe("name");
        decodedHeader.Columns[1].Type.ShouldBe((byte)9);

        var complete = new ProtocolResultCompleteMessage(42);
        ProtocolResultCompleteMessage.Decode(complete.Encode()).AffectedCount.ShouldBe(42);
        ProtocolExecuteMessage.Create("X").Encode().ShouldBe(new byte[] { 0, 0, 0, 1, 88, 0, 0, 0, 0 });
        new ProtocolResultHeaderMessage(new[] { ("id", (byte)5) }).Encode()
            .ShouldBe(new byte[] { 0, 0, 0, 1, 0, 0, 0, 2, 105, 100, 5 });
        new ProtocolResultCompleteMessage(42).Encode().ShouldBe(new byte[] { 0, 0, 0, 0, 0, 0, 0, 42 });
        Should.Throw<ProtocolException>(() => ProtocolExecuteMessage.Decode(new byte[] { 0, 0, 0, 5, 65 }));
        Should.Throw<ProtocolException>(() => ProtocolResultCompleteMessage.Decode(new byte[] { 1, 2 }));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Protocol: a parameter length that overflows the bounds check is rejected")]
    public void Decode_ParameterLengthOverflowingTheBoundsCheck_ShouldThrowProtocolException()
    {
        // Arrange: empty statement, one parameter, empty name, length = int.MaxValue.
        // The old guard computed position + length, which wraps negative and passes.
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00, // statement: zero-length string
            0x00, 0x00, 0x00, 0x01, // parameter count: 1
            0x00, 0x00, 0x00, 0x00, // parameter name: zero-length string
            0x7F, 0xFF, 0xFF, 0xFF, // parameter length: int.MaxValue
        ];

        // Act / Assert
        Should.Throw<ProtocolException>(() => ProtocolExecuteMessage.Decode(payload));
    }
}