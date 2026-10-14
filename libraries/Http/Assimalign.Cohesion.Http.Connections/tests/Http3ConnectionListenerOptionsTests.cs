using System;

using Shouldly;

using Xunit;

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
