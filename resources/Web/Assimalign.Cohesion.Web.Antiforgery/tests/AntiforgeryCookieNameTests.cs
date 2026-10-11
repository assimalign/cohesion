using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

public class AntiforgeryCookieNameTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: An invalid CookieName fails before feature registration")]
    [MemberData(nameof(InvalidCookieNames))]
    public void AddAntiforgery_CookieNameOutsideGrammar_ShouldThrowAtRegistration(string? cookieName, bool useDataProtection)
    {
        // Arrange
        IServiceProviderBuilder services = new ServiceProviderBuilder();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            services.AddAntiforgery(antiforgery =>
            {
                antiforgery.Options.CookieName = cookieName!;
                if (useDataProtection)
                {
                    antiforgery.UseDataProtection(DataProtectionProvider.Create(new InMemoryKeyRepository()));
                }
            }));

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldContain("HttpAntiforgeryOptions.CookieName", Case.Sensitive);
        exception.InnerException.ShouldBeAssignableTo<ArgumentException>().ShouldNotBeNull().ParamName.ShouldBe("name");
        if (!string.IsNullOrEmpty(cookieName))
        {
            exception.ToString().ShouldNotContain(cookieName, Case.Sensitive);
        }

        services.Container.Count.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - AddAntiforgery: A token CookieName reaches the registered service and issued cookie")]
    [InlineData("sid", false)]
    [InlineData("sid", true)]
    [InlineData(".Cohesion.Session", false)]
    [InlineData(".Cohesion.Session", true)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", true)]
    public void AddAntiforgery_CookieNameInGrammar_ShouldRegisterServiceWithName(string cookieName, bool useDataProtection)
    {
        // Arrange
        IServiceProviderBuilder services = new ServiceProviderBuilder();

        // Act
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Options.CookieName = cookieName;
            if (useDataProtection)
            {
                antiforgery.UseDataProtection(DataProtectionProvider.Create(new InMemoryKeyRepository()));
            }
        });
        IServiceProvider provider = services.Build();
        IHttpFeature feature = provider.GetRequiredService<IEnumerable<IHttpFeature>>().ShouldHaveSingleItem();
        IHttpAntiforgery service = feature.ShouldBeAssignableTo<IHttpAntiforgeryFeature>().ShouldNotBeNull().Antiforgery;
        AntiforgeryTestContext context = new(HttpMethod.Get);
        HttpAntiforgeryTokenSet tokens = service.GetAndStoreTokens(context);

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe(cookieName);
        cookie.Value.ShouldBe(tokens.CookieToken);
    }

    public static TheoryData<string?, bool> InvalidCookieNames
    {
        get
        {
            TheoryData<string?, bool> data = new();
            foreach (string? name in new string?[]
            {
                null, "", "a b", "a\tb", "a=b", "a;b", "a,b", "a/b", "a\\b",
                "a\"b", "a\r\nb", "a\0b", "a\u007fb", "caf\u00e9",
            })
            {
                data.Add(name, false);
                data.Add(name, true);
            }

            return data;
        }
    }
}
