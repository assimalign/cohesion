using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class Http3ListenerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3ListenerOptions: Defaults to the static-only QPACK profile")]
    public void Options_OnCreate_DefaultToStaticOnly()
    {
        Http3ListenerOptions options = new();

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
}
