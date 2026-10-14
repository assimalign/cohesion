using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlExceptionMessageTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - ParseDocument: undefined alias preserves message and position")]
    public void ParseDocument_UndefinedAlias_PreservesMessageAndPosition()
    {
        // Arrange
        const string text = "\n*missing";

        // Act
        Action parse = () => YamlText.ParseDocument(text);

        // Assert
        var exception = Should.Throw<YamlException>(parse);
        exception.Message.ShouldBe("Alias '*missing' refers to an undefined anchor. (line 2, column 1)");
        exception.Line.ShouldBe(2);
        exception.Column.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Mapping indexer: missing key preserves message")]
    public void Indexer_MissingKey_PreservesMessage()
    {
        // Arrange
        var mapping = new YamlMapping();

        // Act
        Action read = () => _ = mapping["missing"];

        // Assert
        Should.Throw<KeyNotFoundException>(read).Message.ShouldBe("The mapping contains no 'missing' key.");
    }
}
