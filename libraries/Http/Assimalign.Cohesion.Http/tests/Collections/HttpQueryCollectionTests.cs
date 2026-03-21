using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpQueryCollectionTests
{
    [Fact]
    public void Indexer_MissingKey_ShouldReturnEmptyValue()
    {
        // Arrange
        HttpQueryCollection collection = new();

        // Act
        HttpQueryValue value = collection["missing"];

        // Assert
        value.ShouldBe(HttpQueryValue.Empty);
    }

    [Fact]
    public void Indexer_SetValue_ShouldBeAvailableThroughContainsKeyAndTryGetValue()
    {
        // Arrange
        HttpQueryCollection collection = new();

        // Act
        collection["page"] = "10";
        bool found = collection.TryGetValue("page", out HttpQueryValue value);

        // Assert
        collection.ContainsKey("page").ShouldBeTrue();
        found.ShouldBeTrue();
        value.Value.ShouldBe("10");
    }

    [Fact]
    public void MutatingCollection_WhenReadOnly_ShouldThrowInvalidOperationException()
    {
        // Arrange
        HttpQueryCollection collection = new()
        {
            IsReadOnly = true
        };

        // Act
        Action action = () => collection["page"] = "10";

        // Assert
        action.ShouldThrow<InvalidOperationException>()
            .Message.ShouldBe("The query collection cannot be modified because it is read-only.");
    }
}
