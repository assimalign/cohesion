using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceSourceProviderTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Source provider: default members throw NotSupportedException")]
    public async Task ReadAsync_DefaultMembers_ShouldThrowNotSupportedException()
    {
        // Arrange
        IResourceSourceProvider provider = new EmptySourceProvider();
        ResourceSourceRequest request = CreateRequest(ResourceMountKind.Secret);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act & Assert
        NotSupportedException secret = await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadSecretAsync(request, timeout.Token));
        NotSupportedException certificate = await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadCertificateAsync(request, timeout.Token));
        NotSupportedException configuration = await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadConfigurationAsync(CreateRequest(ResourceMountKind.Configuration), timeout.Token));
        secret.Message.ShouldContain(nameof(EmptySourceProvider), Case.Sensitive);
        certificate.Message.ShouldContain("certificate", Case.Sensitive);
        configuration.Message.ShouldContain("configuration", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Source provider: an implemented member replaces only its default")]
    public async Task ReadSecretAsync_Implemented_ShouldReturnValueWhileOthersStayUnsupported()
    {
        // Arrange
        IResourceSourceProvider provider = new SecretOnlySourceProvider();
        ResourceSourceRequest request = CreateRequest(ResourceMountKind.Secret);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        ReadOnlyMemory<byte> secret = await provider.ReadSecretAsync(request, timeout.Token);

        // Assert
        secret.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadCertificateAsync(request, timeout.Token));
        await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadConfigurationAsync(request, timeout.Token));
    }

    private static ResourceSourceRequest CreateRequest(ResourceMountKind kind) =>
        new("appa", "api", "db", kind, "db-password", Store: null);
}
