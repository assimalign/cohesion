using System;
using System.Collections.Generic;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Dns.Tests;

public class DnsNameEncodingCompatibilityTests
{
    [Theory(DisplayName = "Cohesion Test [Dns] - Name encoding: Should preserve literal label bytes and ASCII fallback")]
    [InlineData(".")]
    [InlineData("Example.COM.")]
    [InlineData("MiXeD.service.example")]
    [InlineData("\u00c4.\u03a3.example")]
    [InlineData("\ud83d\ude00.example")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.test")]
    public void WriteTo_Name_ShouldPreserveWireBytes(string value)
    {
        // Arrange
        DnsName name = new(value);
        DnsHeader header = new(42, 0, DnsOpCode.Query, DnsResponseCode.NoError, 0, 0, 0, 0);
        DnsMessage message = new(header, new[] { new DnsQuestion(name, DnsRecordType.A) }, [], [], []);
        List<byte> expected = new();
        foreach (string label in name.GetLabels())
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            expected.Add((byte)bytes.Length);
            expected.AddRange(bytes);
        }
        expected.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        byte[] buffer = new byte[512];

        // Act
        int written = message.WriteTo(buffer);

        // Assert
        buffer.AsSpan(DnsHeader.WireSize, written - DnsHeader.WireSize).ToArray().ShouldBe(expected.ToArray());
    }
}
