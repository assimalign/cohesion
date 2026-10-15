using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

public class OutputCacheVaryMarkerTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Caching] - OutputCacheEntry: Invalid stored Vary names fail before lookup")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X Tenant")]
    [InlineData(":authority")]
    [InlineData("X-Tenant\r\nInjected")]
    public void Constructor_InvalidVaryName_ShouldThrowBeforeLookup(string? name)
    {
        // Arrange
        Action construct = () => _ = new OutputCacheEntry(HttpStatusCode.Ok, [], null,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), [], [name!]);

        // Act and assert
        ArgumentException exception = construct.ShouldThrow<ArgumentException>();
        exception.ParamName.ShouldBe("varyBy");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - OutputCacheEntry: Later store mutations cannot replace Vary names")]
    public void Constructor_VaryNamesChangedAfterConstruction_ShouldKeepValidatedSnapshot()
    {
        // Arrange
        string[] names = ["X-Tenant"];
        OutputCacheEntry entry = new(HttpStatusCode.Ok, [], null,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), [], names);

        // Act
        names[0] = "";

        // Assert
        entry.VaryBy.ShouldBe(["X-Tenant"]);
        Should.Throw<NotSupportedException>(() => ((IList<string>)entry.VaryBy)[0] = "");
    }
}
