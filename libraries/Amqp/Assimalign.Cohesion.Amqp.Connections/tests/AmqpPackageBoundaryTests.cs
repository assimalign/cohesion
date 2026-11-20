using System;
using System.Linq;
using System.Reflection;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Locks the engine's boundary (#401): it uses no Cohesion assembly but the model, <c>Connections</c> and
/// <c>Core</c>, so it never depends on a carrier driver, a TLS layer or a host; those are composed by the
/// application that creates the listener or factory.
/// </summary>
public class AmqpPackageBoundaryTests
{
    private static readonly string[] _allowed = ["Assimalign.Cohesion.Amqp", "Assimalign.Cohesion.Connections", "Assimalign.Cohesion.Core"];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Assembly: Should use no Cohesion assembly but the model, Connections and Core")]
    public void Assembly_OnEnginePackage_ShouldUseOnlyTheModelConnectionsAndCore()
    {
        // Arrange
        Assembly engine = typeof(AmqpConnectionListener).Assembly;

        // Act
        string[] references =
        [
            .. engine.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => name.StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal))
        ];

        // Assert
        references.ShouldContain("Assimalign.Cohesion.Amqp");
        references.ShouldBeSubsetOf(_allowed);
    }
}
