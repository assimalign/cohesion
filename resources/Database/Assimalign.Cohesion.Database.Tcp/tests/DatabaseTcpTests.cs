using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Tcp.AotSample;

namespace Assimalign.Cohesion.Database.Tcp.Tests;

public sealed class DatabaseTcpTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Named platform endpoints serve every model and explicit listeners win (#1790)")]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)]
    public async Task Create_PlatformEndpoint_ShouldServeHandshakeAndPing(int model, bool explicitListener)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var result = await TcpServerSmoke.RunAsync(model, timeout.Token, usePlatformEndpoint: true, explicitListener);

        // Assert
        result.Responses.ShouldBe([ProtocolMessageType.Authenticate, ProtocolMessageType.Ready, ProtocolMessageType.Pong]);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - A missing or malformed named endpoint retains the missing-listener refusal (#1790)")]
    [InlineData(0, "")] [InlineData(1, "")] [InlineData(2, "")] [InlineData(3, "")] [InlineData(4, "")]
    [InlineData(0, "0")] [InlineData(1, "65536")] [InlineData(2, "bad")] [InlineData(3, "-1")] [InlineData(4, "5740")]
    public async Task Create_IncompletePlatformEndpoint_ShouldRequireListener(int model, string port)
    {
        // Arrange: http is deliberately present; a wire server must never consume it.
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5741",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "PORT")] = port,
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "SCHEME")] = port == "5740" ? "" : "cohesion-db",
        }, CancellationToken.None));
        await using var engine = NewEngine(model);

        // Act
        var failure = Should.Throw<ArgumentException>(() => NewServer(model, engine, null, 100));

        // Assert
        failure.ParamName.ShouldBe("Listener");
        failure.Message.ShouldStartWith("CK08001:", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Platform binding reuses Listen host validation (#1790)")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("example.test", null)]
    public async Task Create_PlatformHost_ShouldUseLiteralAddressPolicy(string host, string? address)
    {
        // Arrange
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Endpoint("sql", "HOST")] = host,
            [AppEnvironment.Variables.Endpoint("sql", "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint("sql", "SCHEME")] = "tcp",
        }, CancellationToken.None));
        await using var engine = SqlDatabaseEngine.Create("platform-host", new());
        var options = new SqlDatabaseServerOptions();

        // Act / Assert
        if (address is null)
        {
            Should.Throw<InvalidOperationException>(() => SqlDatabaseServer.Create(engine, options))
                .Message.ShouldStartWith("CK07101:", Case.Sensitive);
        }
        else
        {
            var resolved = new PlatformServerOptions();
            resolved.Validate();
            resolved.Listener.ShouldNotBeNull().EndPoint.ShouldBe(new IPEndPoint(IPAddress.Parse(address), 5740));
            await resolved.Listener.DisposeAsync();
            await using var server = SqlDatabaseServer.Create(engine, options);
            options.Listener.ShouldBeNull(); // The server owns a snapshot, not the caller's options.
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Concrete builder options bind and serve every model over TCP (#1391)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Listen_ModelBuilder_ShouldServeHandshakeAndPing(int model)
    {
        // Thirty-second hang guard; no elapsed-time assertion decides the outcome.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TcpServerSmoke.RunAsync(model, timeout.Token);
        result.Endpoint.Address.ShouldBe(IPAddress.Loopback);
        result.Endpoint.Port.ShouldBeGreaterThan(0);
        result.Responses.ShouldBe([ProtocolMessageType.Authenticate, ProtocolMessageType.Ready, ProtocolMessageType.Pong]);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Resource hosts preserve IP, localhost and wildcard binding (#1391)")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("[::1]", "::1")]
    [InlineData("[::]", "::")]
    public async Task Listen_LiteralHost_ShouldConfigureExpectedAddress(string host, string address)
    {
        var options = new GraphDatabaseServerOptions();
        GraphDatabaseServerOptions result = options.Listen(new Uri($"cohesion-db://{host}:5740"));
        result.ShouldBeSameAs(options);
        result.Listener.ShouldNotBeNull().EndPoint.ShouldBe(new IPEndPoint(IPAddress.Parse(address), 5740));
        await result.Listener.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Tcp] - Invalid endpoints and DNS hosts are refused before listener creation (#1391)")]
    public void Listen_InvalidEndpoint_ShouldPreserveArgumentsAndRefuseDns()
    {
        var options = new BlobDatabaseServerOptions();
        Should.Throw<ArgumentNullException>(() => options.Listen(null!)).ParamName.ShouldBe("endpoint");
        Should.Throw<ArgumentNullException>(() => ((GraphDatabaseServerOptions)null!).Listen(new Uri("cohesion-db://127.0.0.1:1"))).ParamName.ShouldBe("options");
        Should.Throw<ArgumentException>(() => options.Listen(new Uri("relative", UriKind.Relative)));
        Should.Throw<ArgumentException>(() => options.Listen(new Uri("cohesion-db://127.0.0.1:0")));
        var dns = Should.Throw<InvalidOperationException>(() => options.Listen(new Uri("cohesion-db://example.test:5740")));
        dns.Message.ShouldStartWith("CK07101:", Case.Sensitive);
        options.Listener.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Server option refusals identify model, engine and parameter (#1391)")]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    public async Task Create_InvalidOptions_ShouldNameModelEngineAndOption(int model, bool missingListener)
    {
        await using var engine = NewEngine(model);
        await using var listener = TcpConnectionListener.Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        var failure = Should.Throw<ArgumentException>(() => NewServer(model, engine, missingListener ? null : listener, missingListener ? 100 : 0));
        failure.ParamName.ShouldBe(missingListener ? "Listener" : "MaxSessions");
        failure.Message.ShouldContain(TcpServerSmoke.Models[model], Case.Sensitive);
        failure.Message.ShouldContain(engine.Name.ToString(), Case.Sensitive);
        failure.Message.ShouldStartWith(missingListener ? "CK08001:" : "CK08002:", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Drain refusals retain timer bounds and identify their option (#1391)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Create_InvalidDrain_ShouldNameTimeoutAndEngine(int model)
    {
        await using var engine = NewEngine(model);
        await using var listener = TcpConnectionListener.Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        var failure = Should.Throw<ArgumentOutOfRangeException>(() => NewServer(model, engine, listener, 100, TimeSpan.Zero));
        failure.ParamName.ShouldBe("ShutdownDrainTimeout");
        failure.Message.ShouldContain(engine.Name.ToString(), Case.Sensitive);
        failure.Message.ShouldContain(TcpServerSmoke.Models[model], Case.Sensitive);
        failure.Message.ShouldStartWith("CK08003:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Tcp] - Shared options preserve model-specific zero and infinite timer policies (#1391)")]
    public async Task Create_ModelTimerPolicies_ShouldRemainUnchanged()
    {
        await using var documents = DocumentDatabaseEngine.Create("zero-document", new());
        await using var documentListener = TcpConnectionListener.Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var documentServer = DocumentDatabaseServer.Create(documents, new()
        { Listener = documentListener, AuthenticationTimeout = TimeSpan.Zero, IdleTimeout = TimeSpan.Zero });
        await using var blobs = BlobDatabaseEngine.Create("zero-blob", new());
        await using var blobListener = TcpConnectionListener.Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => BlobDatabaseServer.Create(blobs, new()
        { Listener = blobListener, AuthenticationTimeout = TimeSpan.Zero })).ParamName.ShouldBe("AuthenticationTimeout");
        await using var blobServer = BlobDatabaseServer.Create(blobs, new()
        { Listener = blobListener, AuthenticationTimeout = Timeout.InfiniteTimeSpan, IdleTimeout = Timeout.InfiniteTimeSpan });
    }

    private sealed class PlatformServerOptions : DatabaseServerOptions
    {
        public void Validate() => ValidateCommon("SQL", "platform-host");
    }

    private static DatabaseEngine NewEngine(int model) => model switch
    {
        0 => SqlDatabaseEngine.Create("guard-" + TcpServerSmoke.Models[model], new()),
        1 => GraphDatabaseEngine.Create("guard-" + TcpServerSmoke.Models[model], new()),
        2 => DocumentDatabaseEngine.Create("guard-" + TcpServerSmoke.Models[model], new()),
        3 => BlobDatabaseEngine.Create("guard-" + TcpServerSmoke.Models[model], new()),
        4 => KeyValueDatabaseEngine.Create("guard-" + TcpServerSmoke.Models[model], new()),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    private static DatabaseServer NewServer(int model, DatabaseEngine engine, IDuplexConnectionListener? listener, int max, TimeSpan? drain = null) => model switch
    {
        0 => SqlDatabaseServer.Create((SqlDatabaseEngine)engine, new() { Listener = listener, MaxSessions = max, ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30) }),
        1 => GraphDatabaseServer.Create((GraphDatabaseEngine)engine, new() { Listener = listener, MaxSessions = max, ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30) }),
        2 => DocumentDatabaseServer.Create((DocumentDatabaseEngine)engine, new() { Listener = listener, MaxSessions = max, ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30) }),
        3 => BlobDatabaseServer.Create((BlobDatabaseEngine)engine, new() { Listener = listener, MaxSessions = max, ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30) }),
        4 => KeyValueDatabaseServer.Create((KeyValueDatabaseEngine)engine, new() { Listener = listener, MaxSessions = max, ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(30) }),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };
}
