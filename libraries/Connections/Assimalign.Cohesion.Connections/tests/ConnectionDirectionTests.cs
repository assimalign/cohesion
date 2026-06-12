using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionDirectionTests
{
    [Fact]
    public void Values_OnConnectionDirection_ShouldRemainStable()
    {
        // Arrange & Act & Assert
        ((int)ConnectionDirection.Bidirectional).ShouldBe(0);
        ((int)ConnectionDirection.ReadOnly).ShouldBe(1);
        ((int)ConnectionDirection.WriteOnly).ShouldBe(2);
    }
}
