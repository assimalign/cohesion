using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.SecretStore.Client.Tests.TestObjects;

namespace Assimalign.Cohesion.SecretStore.Client.Tests;

public class SecretStoreCertificateReferenceTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Client] - TryCreate: Preserves supported certificate references")]
    [InlineData("ca/root")]
    [InlineData("certs/leaf")]
    [InlineData("certs/Leaf.Name")]
    public void TryCreate_ValidReference_ShouldPreserveValue(string name)
    {
        // Arrange / Act
        bool created = SecretStoreCertificateReference.TryCreate(name, out var reference);

        // Assert
        created.ShouldBeTrue();
        reference.Value.ShouldBe(name);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Client] - Equals: Certificate identifiers remain case-sensitive")]
    public void Equals_DifferentCase_ShouldBeFalse()
    {
        // Arrange
        SecretStoreCertificateReference lower = "certs/leaf";
        SecretStoreCertificateReference upper = "certs/Leaf";

        // Act
        bool equal = lower == upper;

        // Assert
        equal.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Client] - GetCertificateAsync: Default reference fails before HTTP")]
    public async Task GetCertificateAsync_DefaultReference_ShouldRefuseBeforeSending()
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
        Func<Task> action = () => client.GetCertificateAsync(default, CancellationToken.None);

        // Assert
        (await Should.ThrowAsync<ArgumentException>(action)).ParamName.ShouldBe("name");
        requests.ShouldBe(0);
    }
}
