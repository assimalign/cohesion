using System;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public sealed class ProtocolPayloadTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Strings reject invalid UTF-8 without replacement")]
    [InlineData("FF")]
    [InlineData("C080")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("E282")]
    public void ReadString_MalformedUtf8_ShouldThrowProtocolException(string hex)
    {
        // Arrange
        byte[] bytes = Convert.FromHexString(hex);
        byte[] payload = [0, 0, 0, (byte)bytes.Length, .. bytes];
        int position = 0;

        // Act
        var error = Should.Throw<ProtocolException>(() => ProtocolPayload.ReadString(payload, ref position));

        // Assert
        error.InnerException.ShouldBeOfType<DecoderFallbackException>();
        position.ShouldBe(4);
    }
}
