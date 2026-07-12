using System.Net;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// Tests for the <c>key=value;</c> connection-string surface.
/// </summary>
public class DatabaseConnectionSettingsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: connection strings parse database, principal, endpoint, and pool size")]
    public void Parse_FullConnectionString_ShouldPopulateSettings()
    {
        // Act
        var settings = DatabaseConnectionSettings.Parse("Database=app;Principal=chase;Endpoint=db.example.test:9042;MaxPoolSize=3");

        // Assert
        settings.Database.ShouldBe("app");
        settings.Principal.ShouldBe("chase");
        settings.MaxPoolSize.ShouldBe(3);

        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("db.example.test");
        endpoint.Port.ShouldBe(9042);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: endpoints without a port take the default port")]
    public void Parse_EndpointWithoutPort_ShouldUseDefaultPort()
    {
        // Act
        var settings = DatabaseConnectionSettings.Parse("Database=app;Endpoint=localhost");

        // Assert
        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("localhost");
        endpoint.Port.ShouldBe(DatabaseConnectionSettings.DefaultPort);
        settings.Principal.ShouldBe("anonymous"); // default identity
        settings.MaxPoolSize.ShouldBe(DatabaseConnectionSettings.DefaultMaxPoolSize);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: unknown keys and malformed pairs fail loudly")]
    public void Parse_UnknownKeyOrMalformedPair_ShouldThrow()
    {
        // Act / Assert
        Should.Throw<DatabaseClientException>(() => DatabaseConnectionSettings.Parse("Database=app;Driver=tcp"))
            .Message.ShouldContain("Driver");

        Should.Throw<DatabaseClientException>(() => DatabaseConnectionSettings.Parse("Database"));

        Should.Throw<DatabaseClientException>(() => DatabaseConnectionSettings.Parse("Database=app;MaxPoolSize=zero"))
            .Code.ShouldBe(ProtocolErrorCode.Internal);
    }
}
