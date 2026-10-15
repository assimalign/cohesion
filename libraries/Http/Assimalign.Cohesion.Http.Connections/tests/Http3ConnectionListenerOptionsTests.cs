using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class Http3ConnectionListenerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3ConnectionListenerOptions: Defaults to the static-only QPACK profile")]
    public void Options_OnCreate_DefaultToStaticOnly()
    {
        Http3ConnectionListenerOptions options = new();

        options.QPack.ShouldNotBeNull();
        options.QPack.MaxTableCapacity.ShouldBe(0);
        options.QPack.MaxBlockedStreams.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Round-trips assigned capacity and blocked streams")]
    public void QPack_OnAssignment_RoundTrips()
    {
        Http3QPackOptions qpack = new()
        {
            MaxTableCapacity = 4096,
            MaxBlockedStreams = 16,
        };

        qpack.MaxTableCapacity.ShouldBe(4096);
        qpack.MaxBlockedStreams.ShouldBe(16);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Rejects a negative table capacity")]
    public void QPack_OnNegativeCapacity_Throws()
    {
        Http3QPackOptions qpack = new();

        Should.Throw<ArgumentOutOfRangeException>(() => qpack.MaxTableCapacity = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Rejects a negative blocked-stream limit")]
    public void QPack_OnNegativeBlockedStreams_Throws()
    {
        Http3QPackOptions qpack = new();

        Should.Throw<ArgumentOutOfRangeException>(() => qpack.MaxBlockedStreams = -1);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Rejects a table capacity above the wire range")]
    [InlineData(1L << 62)]
    [InlineData(long.MaxValue)]
    public void MaxTableCapacity_OnValueAboveWireRange_ShouldThrow(long value)
    {
        // Arrange
        Http3QPackOptions qpack = new() { MaxTableCapacity = 4096 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => qpack.MaxTableCapacity = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        qpack.MaxTableCapacity.ShouldBe(4096);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Rejects a blocked-stream limit above the wire range")]
    [InlineData(1L << 62)]
    [InlineData(long.MaxValue)]
    public void MaxBlockedStreams_OnValueAboveWireRange_ShouldThrow(long value)
    {
        // Arrange
        Http3QPackOptions qpack = new() { MaxBlockedStreams = 16 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => qpack.MaxBlockedStreams = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        qpack.MaxBlockedStreams.ShouldBe(16);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Boundary settings round-trip over the wire")]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(1L, (1L << 62) - 1)]
    [InlineData((1L << 62) - 1, (1L << 62) - 1)]
    public void EncodePayload_OnBoundaryQPackSettings_ShouldRoundTrip(long capacity, long blockedStreams)
    {
        // Arrange
        Http3QPackOptions qpack = new()
        {
            MaxTableCapacity = capacity,
            MaxBlockedStreams = blockedStreams,
        };
        Dictionary<long, long> settings = new();

        // Act
        byte[] payload = Http3LocalSettings.EncodePayload(qpack);
        int index = 0;
        while (index < payload.Length)
        {
            long identifier = QuicVariableLengthInteger.Decode(payload, ref index);
            settings.Add(identifier, QuicVariableLengthInteger.Decode(payload, ref index));
        }

        // Assert
        settings[Http3SettingId.QPackMaxTableCapacity].ShouldBe(capacity);
        if (capacity > 0)
        {
            settings[Http3SettingId.QPackBlockedStreams].ShouldBe(blockedStreams);
        }
        else
        {
            settings.ContainsKey(Http3SettingId.QPackBlockedStreams).ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Boundary table capacity round-trips through a prefixed instruction")]
    [InlineData(0L)]
    [InlineData((1L << 62) - 1)]
    public void MaxTableCapacity_OnBoundaryValue_ShouldRoundTripPrefixedInstruction(long capacity)
    {
        // Arrange
        Http3QPackOptions qpack = new() { MaxTableCapacity = capacity };
        QPackDynamicTable table = new(qpack.MaxTableCapacity);
        using MemoryStream instruction = new();

        // Act
        QPackPrefixedInteger.Encode(instruction, capacity, 5, 0b0010_0000);
        byte[] encoded = instruction.ToArray();
        bool complete = QPackEncoderInstructionParser.TryApplyNext(encoded, table, out int consumed, out bool inserted);

        // Assert
        complete.ShouldBeTrue();
        consumed.ShouldBe(encoded.Length);
        inserted.ShouldBeFalse();
        table.Capacity.ShouldBe(capacity);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Defaults the decoded field-section size to 16 KB")]
    public void MaxFieldSectionSize_OnCreate_ShouldDefaultTo16KB()
    {
        Http3QPackOptions qpack = new();

        qpack.MaxFieldSectionSize.ShouldBe(16 * 1024);
        Http3QPackOptions.DefaultMaxFieldSectionSize.ShouldBe(16 * 1024);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Round-trips an assigned decoded field-section size")]
    public void MaxFieldSectionSize_OnAssignment_ShouldRoundTrip()
    {
        Http3QPackOptions qpack = new()
        {
            MaxFieldSectionSize = 64 * 1024,
        };

        qpack.MaxFieldSectionSize.ShouldBe(64 * 1024);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3QPackOptions: Rejects a decoded field-section size no SETTINGS value can carry")]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L << 62)]
    public void MaxFieldSectionSize_OnOutOfRangeValue_ShouldThrow(long value)
    {
        Http3QPackOptions qpack = new();

        Should.Throw<ArgumentOutOfRangeException>(() => qpack.MaxFieldSectionSize = value);
    }
}
