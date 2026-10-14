using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

// Contract coverage for the new value type; injection regressions live separately
// so they can also be compiled and executed against the pre-fix public API.
public class HttpCookieExtensionTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Every allowed ASCII character is preserved")]
    public void TryCreate_AllExtensionCharacters_ShouldPreserveValue()
    {
        // Arrange
        string candidate = new(Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Where(code => code != ';').Select(code => (char)code).ToArray());

        // Act
        bool valid = HttpCookieExtension.IsValid(candidate, out string? message);
        bool created = HttpCookieExtension.TryCreate(candidate, out HttpCookieExtension extension);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        created.ShouldBeTrue();
        extension.Value.ShouldBe(candidate);
        HttpCookieOptions options = new() { Path = null };
        options.Extensions.Add(extension);
        new HttpCookie("id", "1", options).ToString().ShouldBe("id=1; " + candidate);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Invalid candidates return false without an instance")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x; Domain=evil.example")]
    [InlineData("x\r")]
    [InlineData("x\n")]
    [InlineData("x\0")]
    [InlineData("x\u007F")]
    [InlineData("x\u0080")]
    [InlineData("x\uD800")]
    public void TryCreate_InvalidExtension_ShouldReturnFalse(string? candidate)
    {
        // Arrange / Act
        bool valid = HttpCookieExtension.IsValid(candidate!, out string? message);
        bool created = HttpCookieExtension.TryCreate(candidate, out HttpCookieExtension extension);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrEmpty();
        created.ShouldBeFalse();
        extension.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Extensions: Default is refused on every list mutation path")]
    [InlineData("Add")]
    [InlineData("Insert")]
    [InlineData("Set")]
    public void Extensions_DefaultItem_ShouldRejectWithoutMutation(string operation)
    {
        // Arrange
        HttpCookieOptions options = new();
        options.Extensions.Add("Priority=High");
        HttpCookieExtension empty = default;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
        {
            switch (operation)
            {
                case "Add": options.Extensions.Add(empty); break;
                case "Insert": options.Extensions.Insert(0, empty); break;
                case "Set": options.Extensions[0] = empty; break;
            }
        });

        // Assert
        exception.ParamName.ShouldBe("item");
        options.Extensions.ShouldHaveSingleItem().Value.ShouldBe("Priority=High");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Copy: Extensions are independent validated snapshots")]
    public void Copy_TypedExtensions_ShouldPreserveIndependentValidatedLists()
    {
        // Arrange
        HttpCookieOptions source = new();
        source.Extensions.Add("Priority=High");

        // Act
        HttpCookieOptions copy = new(source);
        HttpCookie cookie = new("id", "1", source);
        source.Extensions[0] = "Priority=Low";
        copy.Extensions.Add("Partitioned");

        // Assert
        cookie.ToString().ShouldBe("id=1; Path=/; Priority=High");
        new HttpCookie("id", "1", copy).ToString().ShouldBe("id=1; Path=/; Priority=High; Partitioned");
        source.Extensions.ShouldHaveSingleItem().Value.ShouldBe("Priority=Low");
        Should.Throw<ArgumentException>(() => copy.Extensions.Add(default(HttpCookieExtension)));
        Should.Throw<ArgumentException>(() => cookie.Options.Extensions[0] = default(HttpCookieExtension));
    }
}
