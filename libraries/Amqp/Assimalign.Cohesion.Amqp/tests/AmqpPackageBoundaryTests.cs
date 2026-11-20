using System;
using System.Linq;
using System.Reflection;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Locks the model package's boundary (#401): it uses no Cohesion assembly but <c>Core</c> and
/// <c>Connections</c>, so it never depends on the engine, a carrier driver or a host.
/// </summary>
public class AmqpPackageBoundaryTests
{
    private static readonly string[] _allowed = ["Assimalign.Cohesion.Connections", "Assimalign.Cohesion.Core"];

    [Fact(DisplayName = "Cohesion Test [Amqp] - Assembly: Should use no Cohesion assembly but Core and Connections")]
    public void Assembly_OnModelPackage_ShouldUseOnlyCoreAndConnections()
    {
        // Arrange
        Assembly model = typeof(AmqpMessage).Assembly;

        // Act
        string[] references =
        [
            .. model.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => name.StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal))
        ];

        // Assert
        references.ShouldContain("Assimalign.Cohesion.Connections");
        references.ShouldBeSubsetOf(_allowed);
    }
}
