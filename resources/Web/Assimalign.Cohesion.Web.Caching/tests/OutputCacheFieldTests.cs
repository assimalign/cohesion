using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

public class OutputCacheFieldTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Caching] - VaryByHeader: Invalid names fail at composition")]
    [InlineData("")]
    [InlineData("X Tenant")]
    [InlineData(":authority")]
    [InlineData("X-Tenant\r\nInjected")]
    public void VaryByHeader_InvalidName_ShouldThrowAtComposition(string name)
    {
        // Arrange
        OutputCachePolicy policy = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => policy.VaryByHeader(name));
        Should.Throw<ArgumentException>(() => policy.VaryByHeaders.Add(name));
        policy.VaryByHeaders.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - VaryByHeaders: Default is refused on insertion and replacement")]
    public void VaryByHeaders_DefaultName_ShouldRefuseEveryMutation()
    {
        // Arrange
        OutputCachePolicy policy = new();
        policy.VaryByHeaders.Add("X-Tenant");

        // Act and assert
        Should.Throw<ArgumentException>(() => policy.VaryByHeaders.Add(default!));
        Should.Throw<ArgumentException>(() => policy.VaryByHeaders.Insert(0, default!));
        Should.Throw<ArgumentException>(() => policy.VaryByHeaders[0] = default!);
        policy.VaryByHeaders.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - OutputCacheHeader: Malformed store header names fail before replay")]
    [InlineData("X Bad")]
    [InlineData(":status")]
    [InlineData("X\r\nInjected")]
    public void Constructor_InvalidHeaderName_ShouldThrowBeforeReplay(string name)
    {
        // Arrange
        Action construct = () => _ = new OutputCacheHeader(name, ["value"]);

        // Act and assert
        construct.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - OutputCacheHeader: Malformed store values fail before replay")]
    [InlineData("bad\0value")]
    [InlineData("bad\r\nvalue")]
    [InlineData("bad\u007fvalue")]
    [InlineData("bad\u0085value")]
    [InlineData(" value")]
    [InlineData("value\t")]
    public void Constructor_InvalidHeaderValue_ShouldThrowBeforeReplay(string value)
    {
        // Arrange
        Action construct = () => _ = new OutputCacheHeader("X-Test", [value]);

        // Act and assert
        construct.ShouldThrow<ArgumentException>();
    }
}
