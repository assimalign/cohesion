using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the redirect built-in: the RFC 9110 §15.4 status selection per
/// <c>permanent</c> × <c>preserveMethod</c> and the <c>Location</c> header (§10.2.2).
/// </summary>
public class RedirectResultTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Results] - Redirect: permanent × preserveMethod selects the correct 3xx code")]
    [InlineData(false, false, 302)]
    [InlineData(true, false, 301)]
    [InlineData(false, true, 307)]
    [InlineData(true, true, 308)]
    public async Task ExecuteAsync_PermanentPreserveMethodMatrix_SelectsStatus(bool permanent, bool preserveMethod, int expectedStatus)
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Redirect("/moved", permanent, preserveMethod);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(expectedStatus);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/moved");
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Redirect: the typed carrier exposes the derived status")]
    public void Redirect_TypedCarrier_ExposesDerivedStatus()
    {
        // Arrange + Act
        RedirectHttpResult result = TypedResults.Redirect("https://example.com/next", permanent: true, preserveMethod: true);

        // Assert
        result.Url.ShouldBe("https://example.com/next");
        result.Permanent.ShouldBeTrue();
        result.PreserveMethod.ShouldBeTrue();
        result.StatusCode.Value.ShouldBe(308);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Redirect: an empty target is rejected at the factory")]
    public void Factory_EmptyUrl_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentException>(() => Results.Redirect(""));
        Should.Throw<ArgumentException>(() => TypedResults.Redirect(null!));
    }
}
