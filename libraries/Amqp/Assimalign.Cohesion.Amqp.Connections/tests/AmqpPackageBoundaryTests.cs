using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Locks the engine's boundary (#401): it uses and declares no Cohesion assembly but the model,
/// <c>Connections</c> and <c>Core</c>, so it never depends on a carrier driver, a TLS layer or a host; those
/// are composed by the application that creates the listener or factory.
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

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Dependencies: Should declare no Cohesion dependency but the model, Connections and Core")]
    public void Dependencies_OnEnginePackage_ShouldDeclareOnlyTheModelConnectionsAndCore()
    {
        // Arrange
        string dependencyFile = Path.Combine(
            AppContext.BaseDirectory,
            $"{typeof(AmqpPackageBoundaryTests).Assembly.GetName().Name}.deps.json");
        using JsonDocument dependencies = JsonDocument.Parse(File.ReadAllBytes(dependencyFile));

        // Act
        string[]? declared = DeclaredCohesionDependencies(dependencies, "Assimalign.Cohesion.Amqp.Connections");

        // Assert
        declared.ShouldNotBeNull("Assimalign.Cohesion.Amqp.Connections is missing from the test project's dependency file.");
        declared.ShouldContain("Assimalign.Cohesion.Amqp");
        declared.ShouldBeSubsetOf(_allowed);
    }

    // The dependency file lists every reference the project declares, including one nothing uses, which
    // the compiler leaves out of the assembly's metadata and the package's dependencies keep.
    private static string[]? DeclaredCohesionDependencies(JsonDocument dependencies, string package)
    {
        foreach (JsonProperty target in dependencies.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (!library.Name.StartsWith(package + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!library.Value.TryGetProperty("dependencies", out JsonElement declared))
                {
                    return [];
                }

                return
                [
                    .. declared.EnumerateObject()
                        .Select(dependency => dependency.Name)
                        .Where(name => name.StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal))
                ];
            }
        }

        return null;
    }
}
