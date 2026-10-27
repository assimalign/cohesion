using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphMergePatternIdentityTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE logical identity normalizes labels, equalities and numeric tags (#1735)")]
    public void Constructor_EquivalentEqualities_ShouldUseSameResource()
    {
        // Arrange
        var first = new GraphMergePatternIdentity([new(null, ["Person", "Named", "Person"], new Dictionary<string, object?>
        { ["id"] = 7L, ["name"] = "Ada" })], []);
        var labels = new[] { "Named", "Person" };
        var properties = new Dictionary<string, object?> { ["name"] = "Ada", ["id"] = 7m };

        // Act
        var second = new GraphMergePatternIdentity([new(null, labels, properties)], []);
        var resource = second.Resource;
        labels[0] = "Changed";
        properties["id"] = 8;

        // Assert: caller mutation cannot change an acquired identity.
        first.Resource.ShouldBe(resource);
        second.Resource.ShouldBe(resource);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE undirected identity ignores orientation and pattern part ordering (#1735)")]
    public void Constructor_UndirectedReversalAndPartReordering_ShouldUseSameResource()
    {
        // Arrange
        var empty = new Dictionary<string, object?>();
        GraphMergeNodeIdentity[] nodes = [new(10, ["Person"], empty), new(20, ["Person"], empty)];
        var first = new GraphMergePatternIdentity(nodes, [new(0, 1, "KNOWS", true, 30, empty), new(0, 1, "LIKES", false, null, empty)]);

        // Act
        var second = new GraphMergePatternIdentity(nodes, [new(0, 1, "LIKES", false, null, empty), new(1, 0, "KNOWS", true, 30, empty)]);

        // Assert
        second.Resource.ShouldBe(first.Resource);
        second.BoundEntities.ShouldBe([new(true, 10), new(true, 20), new(false, 30)], ignoreOrder: true);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE identity refuses unsupported equality operands (#1735)")]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_InvalidEquality_ShouldCarryStructuredCode(object? value)
    {
        // Arrange
        var properties = new Dictionary<string, object?> { ["id"] = value };

        // Act
        var error = Should.Throw<DatabaseException>(() => new GraphMergePatternIdentity([new(null, ["Person"], properties)], []));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CG05023");
        error.Message.ShouldNotContain("CG05023", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE identity refuses zero bound IDs and invalid topology (#1735)")]
    public void Constructor_InvalidBoundSlots_ShouldRefuse()
    {
        // Arrange
        var empty = new Dictionary<string, object?>();

        // Act / Assert
        Should.Throw<DatabaseException>(() => new GraphMergePatternIdentity([new(0, ["Person"], empty)], [])).Diagnostics[0].Code.ShouldBe("CG05023");
        Should.Throw<DatabaseException>(() => new GraphMergePatternIdentity([new(null, ["Person"], empty)], [new(0, 1, "KNOWS", false, null, empty)]))
            .Diagnostics[0].Code.ShouldBe("CG05023");
        Should.Throw<DatabaseException>(() => new GraphMergePatternIdentity([], [])).Diagnostics[0].Code.ShouldBe("CG05023");
    }
}
