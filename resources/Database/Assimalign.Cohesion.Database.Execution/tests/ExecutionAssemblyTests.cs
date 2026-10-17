using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Execution.Tests;

/// <summary>
/// Tests for the execution package's place in the area: it holds the request and result roots
/// every model shares, and it names no model's query language.
/// </summary>
public class ExecutionAssemblyTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Execution] - DatabaseRequest: the execution package references no language assembly")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only: reads the execution assembly's reference table; test assemblies are not trimmed.")]
    public void GetReferencedAssemblies_ExecutionAssembly_ShouldNotReferenceDatabaseLanguage()
    {
        // Arrange
        var assembly = typeof(DatabaseRequest).Assembly;

        // Act
        string?[] referenced = [.. assembly.GetReferencedAssemblies().Select(name => name.Name)];

        // Assert
        referenced.ShouldNotContain("Assimalign.Cohesion.Database.Language");
        referenced.ShouldContain("Assimalign.Cohesion.Database.Types");
    }
}
