using System;

using Assimalign.Cohesion.DependencyInjection;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

public class AntiforgeryHeaderNameTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: Invalid header names fail before registration")]
    [InlineData("")]
    [InlineData("X CSRF")]
    [InlineData(":method")]
    public void AddAntiforgery_InvalidHeaderName_ShouldRefuseRegistration(string name)
    {
        // Arrange
        IServiceProviderBuilder services = new ServiceProviderBuilder();

        // Act and assert
        Should.Throw<ArgumentException>(() => services.AddAntiforgery(builder => builder.Options.HeaderName = name));
        services.Container.Count.ShouldBe(0);
    }
}
