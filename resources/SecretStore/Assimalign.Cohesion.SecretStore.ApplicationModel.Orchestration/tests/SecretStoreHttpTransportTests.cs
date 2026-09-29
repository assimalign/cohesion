using System.Net.Http;
using System.Net.Security;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreHttpTransportTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - HttpTransport: Disables redirects and cookies and validates with the connection's validator")]
    public void CreateHandler_WithValidator_ShouldConfigureGatewayTransport()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => true;

        // Act
        using SocketsHttpHandler handler = SecretStoreHttpTransport.CreateHandler(validator);

        // Assert
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - HttpTransport: Without a validator the platform default validation applies")]
    public void CreateHandler_WithoutValidator_ShouldKeepDefaultValidation()
    {
        // Arrange / Act
        using SocketsHttpHandler handler = SecretStoreHttpTransport.CreateHandler(null);

        // Assert
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - HttpTransport: The transport owns a new handler")]
    public void Create_Always_ShouldReturnNewTransport()
    {
        // Arrange / Act
        using HttpMessageInvoker first = SecretStoreHttpTransport.Create(null);
        using HttpMessageInvoker second = SecretStoreHttpTransport.Create(null);

        // Assert
        first.ShouldNotBeSameAs(second);
    }
}
