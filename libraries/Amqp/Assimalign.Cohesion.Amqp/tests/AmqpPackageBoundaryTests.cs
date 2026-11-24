using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Locks the model package's boundary (#401): it uses and declares no Cohesion assembly but <c>Core</c>
/// and <c>Connections</c>, so it never depends on the engine, a carrier driver or a host.
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

    [Fact(DisplayName = "Cohesion Test [Amqp] - Dependencies: Should declare no Cohesion dependency but Core and Connections")]
    public void Dependencies_OnModelPackage_ShouldDeclareOnlyCoreAndConnections()
    {
        // Arrange
        string dependencyFile = Path.Combine(
            AppContext.BaseDirectory,
            $"{typeof(AmqpPackageBoundaryTests).Assembly.GetName().Name}.deps.json");
        using JsonDocument dependencies = JsonDocument.Parse(File.ReadAllBytes(dependencyFile));

        // Act
        string[]? declared = DeclaredCohesionDependencies(dependencies, "Assimalign.Cohesion.Amqp");

        // Assert
        declared.ShouldNotBeNull("Assimalign.Cohesion.Amqp is missing from the test project's dependency file.");
        declared.ShouldContain("Assimalign.Cohesion.Connections");
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
