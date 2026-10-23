using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public sealed class ProtocolTransactionStatusTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Transaction status uses exact idle, active and failed bytes (#1540)")]
    [InlineData(ProtocolTransactionStatus.Idle, (byte)'I')]
    [InlineData(ProtocolTransactionStatus.Active, (byte)'T')]
    [InlineData(ProtocolTransactionStatus.Failed, (byte)'E')]
    public void Encode_Status_ShouldRoundTripExactlyOneByte(ProtocolTransactionStatus status, byte wire)
    {
        new ProtocolTransactionStatusMessage(status).Encode().ShouldBe([wire]);
        ProtocolTransactionStatusMessage.Decode([wire]).Status.ShouldBe(status);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Transaction status rejects malformed payloads (#1540)")]
    public void Decode_InvalidStatus_ShouldRejectUnknownTruncatedAndTrailingBytes()
    {
        foreach (byte[] payload in new byte[][] { [], [0], [(byte)'I', 0], [(byte)'X'] })
        { Should.Throw<ProtocolException>(() => ProtocolTransactionStatusMessage.Decode(payload)).Message.ShouldContain("COHDBP002"); }
        Should.Throw<ProtocolException>(() => new ProtocolTransactionStatusMessage((ProtocolTransactionStatus)0).Encode());
    }
}
