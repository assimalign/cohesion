using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// The policy's cookies on a <c>101 Switching Protocols</c>: the upgrade writer sends the <c>Set-Cookie</c>
/// header the policy's collection wrote, one field line per cookie, and nothing besides (#1154).
/// </summary>
public class CookiePolicyProtocolUpgradeTests
{
    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Upgrade: A 101 should send each cookie the policy rewrote once, on its own line")]
    public async Task UseCookiePolicy_CookiesBeforeAcceptedUpgrade_ShouldSendEachRewrittenCookieOnce()
    {
        // Arrange — the default server registers the upgrade interceptor, and the policy adds Secure.
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddCookiePolicy(policy => policy.Options.Secure = CookieSecurePolicy.Always);
        factory.Application.UseCookiePolicy();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
            context.Response.Cookies.Add(new HttpCookie("b", "2", new HttpCookieOptions { HttpOnly = true }));
            IHttpProtocolUpgrade upgrade = context.Upgrade ?? throw new InvalidOperationException("No upgrade was surfaced.");
            await using Stream tunnel = await upgrade.AcceptAsync(context.RequestCancelled);
        });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/chat");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade").ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("Upgrade", "example/1").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        IReadOnlyList<SetCookieField> fields = SetCookieField.ReadAll(response);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.SwitchingProtocols);
        fields.Select(field => field.Raw).ShouldBe(["a=1; Path=/; Secure", "b=2; Path=/; Secure; HttpOnly"]);
    }
}
