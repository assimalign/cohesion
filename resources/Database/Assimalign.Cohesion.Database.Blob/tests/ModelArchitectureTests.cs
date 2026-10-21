using Xunit;

using Assimalign.Cohesion.Database.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[CollectionDefinition(nameof(ModelArchitectureTests), DisableParallelization = true)]
[Collection(nameof(ModelArchitectureTests))]
public sealed class ModelArchitectureTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Architecture: Catalog and Storage preserve their dependency direction")]
    public void Compilation_ModelComponents_ShouldPreserveDependencyDirection()
    {
        // Arrange, Act and Assert: the shared guard reconstructs Csc's compilation and checks resolved symbols.
        ModelArchitecture.Verify();
    }
}
