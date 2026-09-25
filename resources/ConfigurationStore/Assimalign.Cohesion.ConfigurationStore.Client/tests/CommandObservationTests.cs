using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore.Client.Internal;
using Assimalign.Cohesion.ConfigurationStore.Client.Tests.TestObjects;

namespace Assimalign.Cohesion.ConfigurationStore.Client.Tests;

public sealed class CommandObservationTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Client] - Command observation: custom manifest path and refusal detail survive transport")]
    public async Task DeleteCommandAsync_WithCustomControlPlanePath_ShouldReturnProviderDetail()
    {
        // Arrange
        string? uri = null;
        string? method = null;
        string? authorization = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            uri = request.RequestUri!.AbsoluteUri;
            method = request.Method.Method;
            authorization = request.Headers.Authorization!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"status":"Rejected","detail":"Key app/Mode belongs to owner appb."}""", Encoding.UTF8, "application/json"),
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        IConfigurationStoreClient client = new HttpConfigurationStoreClient(
            new Uri("https://configuration.example:8443/custom/control"), new ClientCredential("token"), transport, commandControlPlanePath: true);
        var command = new ResourceCommand("id", "configurationstore.set-value", "appa", "app/Mode", ReadOnlyMemory<byte>.Empty);

        // Act
        ResourceCommandObservation observation = await client.DeleteCommandAsync(command, CancellationToken.None);

        // Assert
        uri.ShouldBe("https://configuration.example:8443/custom/control/commands");
        method.ShouldBe("DELETE");
        authorization.ShouldBe("Bearer token");
        observation.Status.ShouldBe("Rejected");
        observation.Detail.ShouldBe("Key app/Mode belongs to owner appb.");
    }
}
