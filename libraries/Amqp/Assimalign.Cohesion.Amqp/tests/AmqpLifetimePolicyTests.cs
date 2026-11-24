using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Checks the four lifetime policies AMQP 1.0 defines (Part 3, sections 3.5.10 to 3.5.13).
/// </summary>
public class AmqpLifetimePolicyTests
{
    public static TheoryData<AmqpLifetimePolicy, string> Policies => new()
    {
        { AmqpLifetimePolicy.DeleteOnClose, "delete-on-close" },
        { AmqpLifetimePolicy.DeleteOnNoLinks, "delete-on-no-links" },
        { AmqpLifetimePolicy.DeleteOnNoMessages, "delete-on-no-messages" },
        { AmqpLifetimePolicy.DeleteOnNoLinksOrMessages, "delete-on-no-links-or-messages" }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - ToString: Should name each lifetime policy as the specification does")]
    [MemberData(nameof(Policies))]
    public void ToString_OnPolicy_ShouldReturnItsName(AmqpLifetimePolicy policy, string name)
    {
        // Act
        string value = policy.ToString();

        // Assert
        value.ShouldBe(name);
        policy.Descriptor.Value.ShouldBe("amqp:" + name + ":list");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Instances: Should give each policy one instance with its own descriptor")]
    public void Instances_OnEveryPolicy_ShouldBeDistinct()
    {
        // Arrange
        AmqpLifetimePolicy[] policies =
        [
            AmqpLifetimePolicy.DeleteOnClose,
            AmqpLifetimePolicy.DeleteOnNoLinks,
            AmqpLifetimePolicy.DeleteOnNoMessages,
            AmqpLifetimePolicy.DeleteOnNoLinksOrMessages
        ];

        // Act
        AmqpLifetimePolicy again = AmqpLifetimePolicy.DeleteOnClose;

        // Assert
        again.ShouldBeSameAs(AmqpLifetimePolicy.DeleteOnClose);
        policies.ShouldBeUnique();
        policies.Select(policy => policy.Descriptor).ShouldBeUnique();
    }
}
