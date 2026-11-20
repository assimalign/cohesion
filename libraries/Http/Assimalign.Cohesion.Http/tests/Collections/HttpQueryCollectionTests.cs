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

    [Fact]
    public void Source_ParsedCollection_ShouldReturnTheReceivedQueryText()
    {
        // Arrange
        HttpQuery query = new("?q=hello+world&Id=1&id=2&a=1&b=2&a=3&=skipped");

        // Act
        HttpQueryCollection collection = query.Parse();

        // Assert
        collection.Source.ShouldBe(query);
        collection.Source!.Value.Value.ShouldBe("q=hello+world&Id=1&id=2&a=1&b=2&a=3&=skipped");
    }

    [Fact]
    public void Source_ParsedEmptyQuery_ShouldReturnTheEmptyQuery()
    {
        // Arrange
        HttpQuery query = new(string.Empty);

        // Act
        HttpQueryCollection collection = query.Parse();

        // Assert
        collection.Count.ShouldBe(0);
        collection.Source.ShouldBe(query);
    }

    [Fact]
    public void Source_CollectionBuiltInCode_ShouldBeNull()
    {
        // Arrange
        HttpQueryCollection empty = new();
        HttpQueryCollection sized = new(4);
        HttpQueryCollection wrapped = new(new Dictionary<HttpQueryKey, HttpQueryValue> { ["a"] = "1" });

        // Act
        sized.Add("a", "1");

        // Assert
        empty.Source.ShouldBeNull();
        sized.Source.ShouldBeNull();
        wrapped.Source.ShouldBeNull();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("add-pair")]
    [InlineData("indexer")]
    [InlineData("remove")]
    [InlineData("remove-pair")]
    [InlineData("clear")]
    public void Source_AfterMutation_ShouldBeNull(string mutation)
    {
        // Arrange
        HttpQueryCollection collection = new HttpQuery("a=1&b=2").Parse();

        // Act
        switch (mutation)
        {
            case "add":
                collection.Add("c", "3");
                break;
            case "add-pair":
                collection.Add(new KeyValuePair<HttpQueryKey, HttpQueryValue>("c", "3"));
                break;
            case "indexer":
                collection["a"] = "9";
                break;
            case "remove":
                collection.Remove("a").ShouldBeTrue();
                break;
            case "remove-pair":
                collection.Remove(new KeyValuePair<HttpQueryKey, HttpQueryValue>("a", collection["a"])).ShouldBeTrue();
                break;
            case "clear":
                collection.Clear();
                break;
        }

        // Assert
        collection.Source.ShouldBeNull();
    }

    [Fact]
    public void Source_RemoveThatRemovesNothing_ShouldKeepTheSource()
    {
        // Arrange
        HttpQuery query = new("a=1");
        HttpQueryCollection collection = query.Parse();

        // Act
        bool removedKey = collection.Remove("missing");
        bool removedPair = collection.Remove(new KeyValuePair<HttpQueryKey, HttpQueryValue>("a", "2"));
        collection.IsReadOnly = true;

        // Assert
        removedKey.ShouldBeFalse();
        removedPair.ShouldBeFalse();
        collection.Source.ShouldBe(query);
    }

    [Fact]
    public void Source_ReadOnlyMutationThatThrows_ShouldKeepTheSource()
    {
        // Arrange
        HttpQuery query = new("a=1");
        HttpQueryCollection collection = query.Parse();
        collection.IsReadOnly = true;

        // Act
        Action action = () => collection.Add("b", "2");

        // Assert
        action.ShouldThrow<InvalidOperationException>();
        collection.Source.ShouldBe(query);
    }
}
