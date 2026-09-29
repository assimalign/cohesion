using System.Net.Http;
using System.Net.Security;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

public sealed class ConfigurationStoreHttpTransportTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Transport: Disables redirects and cookies and keeps default TLS validation")]
    public void CreateHandler_WithoutValidator_ShouldDisableRedirectsAndCookies()
    {
        // Arrange
        RemoteCertificateValidationCallback? validator = null;

        // Act
        using SocketsHttpHandler handler = ConfigurationStoreHttpTransport.CreateHandler(validator);

        // Assert
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Transport: Installs the gateway's server-certificate validator")]
    public void CreateHandler_WithValidator_ShouldInstallValidator()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => false;

        // Act
        using SocketsHttpHandler handler = ConfigurationStoreHttpTransport.CreateHandler(validator);

        // Assert
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Transport: Creates a disposable transport")]
    public void Create_WithValidator_ShouldReturnTransport()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => false;

        // Act
        using HttpMessageInvoker transport = ConfigurationStoreHttpTransport.Create(validator);

        // Assert
        transport.ShouldNotBeNull();
    }
}
