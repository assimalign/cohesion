using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphFunctionApiReviewTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: registration supports chaining and the shared NULL policy name")]
    public void Add_PublicContract_ShouldMatchOtherModels()
    {
        // Arrange / Act / Assert
        typeof(GraphFunctionCollection).GetMethod(nameof(GraphFunctionCollection.Add))!.ReturnType.ShouldBe(typeof(GraphFunctionCollection));
        Enum.GetNames<GraphNullBehavior>().ShouldContain("ReturnsNullOnNullInput");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: duplicate registration has the shared configuration failure type")]
    public void Add_DuplicateSignature_ShouldThrowInvalidOperation()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("duplicates");
        var function = GraphScalarFunction.Create<long, long>("echo", value => value);
        builder.Functions.Add(function);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(function));
    }
}
