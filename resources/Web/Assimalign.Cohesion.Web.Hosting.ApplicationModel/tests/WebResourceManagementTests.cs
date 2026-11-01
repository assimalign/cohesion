using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// The manage loop on Web: an operator's write through the gateway runs on one instance's control
/// plane directly, is recorded, and reaches every other instance, now or when it next publishes.
/// </summary>
public sealed class WebResourceManagementTests
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.ApplicationModel] - Management: ";

    private const string Partner = "https://partner.example";

    [Fact(DisplayName = DisplayPrefix + "An operator's CORS write through the gateway reaches every instance and one that restarts")]
    public async Task ExecuteAsync_WithCorsWrite_ShouldReachEveryInstanceAndARestartedOne()
    {
        // Arrange: the gateway runs orders-api in its own process, and a platform that scales the
        // resource out starts a second instance, which publishes to the same endpoint.
        await using var infrastructure = new InProcessInfrastructure();
        infrastructure.AddProgram("Web", "orders-api", CorsProgram.MainAsync);

        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local").UseInProcess(infrastructure);
        builder.StartupTimeout = TimeSpan.FromSeconds(30);
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.Build();

        RunningResource? second = null;
        try
        {
            await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            IApplicationGatewayDeployment resource = gateway.Resources.Single();
            var program = new Uri(resource.Outputs[OutputName.Endpoint]);
            (await GetAllowedOriginAsync(program, CorsProgram.Shop)).ShouldBe(CorsProgram.Shop);

            second = await RunningResource.StartInProcessAsync(infrastructure.Endpoint, new WebCorsPolicy { Origins = [CorsProgram.Shop] });
            resource.Instances.Count.ShouldBe(2);

            // Act
            ApplicationActionResult result = await gateway.ExecuteAsync("Web", "orders-api", "cors", ApplicationActionContext.Create(
                ApplicationActionOperation.Write,
                JsonApplicationContentSerializer.Create(),
                Encoding.UTF8.GetBytes($$"""{"origins":["{{Partner}}"]}""")));

            // Assert: the write ran on the instance the gateway runs, the earliest started, and the
            // gateway recorded it and restored it on the second.
            result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            resource.State.ShouldHaveSingleItem().Action.ShouldBe(new ActionName("cors"));
            (await GetAllowedOriginAsync(program, Partner)).ShouldBe(Partner);
            (await second.GetAllowedOriginAsync(Partner)).ShouldBe(Partner);
            resource.Instances.ShouldAllBe(instance => instance.ControlPlaneEndpoint == infrastructure.Endpoint);

            // A restarted instance is a new one: it restores the write from its publication's answer
            // before it serves.
            await second.DisposeAsync();
            second = null;
            await using RunningResource restarted = await RunningResource.StartInProcessAsync(infrastructure.Endpoint, new WebCorsPolicy { Origins = [CorsProgram.Shop] });
            (await restarted.GetAllowedOriginAsync(Partner)).ShouldBe(Partner);
            (await restarted.GetAllowedOriginAsync(CorsProgram.Shop)).ShouldBeNull();
        }
        finally
        {
            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await gateway.StopAsync();
            await gateway.Completion;
        }
    }

    // An instance registers before its servers start, so the first request can arrive before the
    // application listens.
    private static async Task<string?> GetAllowedOriginAsync(Uri address, string origin)
    {
        using var client = new HttpClient { BaseAddress = address };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation("Origin", origin);
                using HttpResponseMessage response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();

                return response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
                    ? values.Single()
                    : null;
            }
            catch (HttpRequestException) when (attempt < 100)
            {
                await Task.Delay(50);
            }
        }
    }
}
