using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpQueryCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpQueryCollection: Add appends and indexer replaces all values")]
    public void Add_OnRepeatedKey_ShouldAppendUntilReplacedOrRemoved()
    {
        // Arrange
        HttpQueryCollection collection = new();

        // Act
        collection.Add("a", "first");
        collection.Add(new KeyValuePair<HttpQueryKey, HttpQueryValue>("a", "second"));
        HttpQueryValue snapshot = collection["a"];
        collection["a"] = "replacement";

        // Assert
        snapshot.ToArray().ShouldBe(new[] { "first", "second" });
        collection["a"].ToArray().ShouldBe(new[] { "replacement" });
        collection.Count.ShouldBe(1);
        collection.Remove("a").ShouldBeTrue();
        collection.ContainsKey("a").ShouldBeFalse();
        snapshot.ToArray().ShouldBe(new[] { "first", "second" });
    }

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
