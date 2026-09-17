using System;
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
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: resource endpoints create typed connection settings")]
    public void For_ResourceEndpoint_ShouldPopulateSettings()
    {
        // Act
        var settings = DatabaseConnectionSettings.For(
            new Uri("tcp://database.internal:5740"),
            database: "orders",
            principal: "orders-api");

        // Assert
        settings.Database.ShouldBe("orders");
        settings.Principal.ShouldBe("orders-api");

        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("database.internal");
        endpoint.Port.ShouldBe(5740);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: resource IP literals create socket-ready DNS endpoints")]
    public void For_ResourceIpEndpoint_ShouldUseUnbracketedSocketHost()
    {
        // Act
        var settings = DatabaseConnectionSettings.For(
            new Uri("cohesion-db://127.0.0.1:5740"));

        // Assert
        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("127.0.0.1");
        endpoint.Port.ShouldBe(5740);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: IPv6 resource endpoints use an unbracketed socket host")]
    public void For_Ipv6ResourceEndpoint_ShouldUseUnbracketedSocketHost()
    {
        // Act
        var settings = DatabaseConnectionSettings.For(
            new Uri("tcp://[::1]:5740"));

        // Assert
        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("::1");
        endpoint.Port.ShouldBe(5740);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: IDN resource endpoints use an ASCII-compatible socket host")]
    public void For_IdnResourceEndpoint_ShouldUseAsciiCompatibleSocketHost()
    {
        // Act
        var settings = DatabaseConnectionSettings.For(
            new Uri("tcp://münich.example:5740"));

        // Assert
        var endpoint = settings.EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("xn--mnich-kva.example");
        endpoint.Port.ShouldBe(5740);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Settings: a null resource endpoint is rejected")]
    public void For_NullResourceEndpoint_ShouldRejectAddress()
    {
        // Act
        Action action = () => DatabaseConnectionSettings.For(null!);

        // Assert
        Should.Throw<ArgumentNullException>(action).ParamName.ShouldBe("endpoint");
    }

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
