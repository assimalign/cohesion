using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public class ProtocolMessageTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Protocol: model payloads preserve legacy encoding")]
    public void Messages_EncodeDecode_ShouldRoundTrip()
    {
        ((byte)SqlProtocolMessageType.Execute).ShouldBe((byte)5);
        ((byte)SqlProtocolMessageType.ResultHeader).ShouldBe((byte)6);
        ((byte)SqlProtocolMessageType.ResultRow).ShouldBe((byte)7);
        ((byte)SqlProtocolMessageType.ResultComplete).ShouldBe((byte)8);
        ((byte)SqlProtocolMessageType.Transaction).ShouldBe((byte)9);

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
}
