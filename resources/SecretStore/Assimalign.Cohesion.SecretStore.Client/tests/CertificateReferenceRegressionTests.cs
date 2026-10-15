using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.SecretStore.Client.Tests.TestObjects;

namespace Assimalign.Cohesion.SecretStore.Client.Tests;

public class CertificateReferenceRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Client] - GetCertificateAsync: Invalid references fail before HTTP")]
    [InlineData("leaf")]
    [InlineData("ca/other")]
    [InlineData("CA/root")]
    [InlineData("ca/root/")]
    [InlineData("Certs/leaf")]
    [InlineData("certs/")]
    [InlineData("certs/ ")]
    [InlineData("certs/.")]
    [InlineData("certs/..")]
    [InlineData("certs/nested/leaf")]
    [InlineData("certs/nested\\leaf")]
    [InlineData(" certs/leaf")]
    public async Task GetCertificateAsync_InvalidReference_ShouldRefuseBeforeSending(string name)
    {
        // Arrange
        int requests = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("certificate")
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        ISecretStoreClient client = SecretStoreClient.Create(
            new Uri("https://secrets.test"), new ClientCredential("token"), transport);

        // Act
        Func<Task> action = () => client.GetCertificateAsync(name, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(action);
        requests.ShouldBe(0);
    }
}
