using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Tcp.AotSample;

namespace Assimalign.Cohesion.Database.Tcp.Tests;

[Collection(nameof(DatabaseProcessEnvironmentCollection))]
public sealed class DatabaseUnmanagedEndpointTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Tcp] - Unmanaged process endpoint values never supply a listener (#1790)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Create_UnmanagedProcessAssignment_ShouldRequireExplicitListener(int model)
    {
        // Arrange: no AppEnvironment scope; these are the current process's contract values.
        var values = new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "",
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint(TcpServerSmoke.Endpoints[model], "SCHEME")] = "tcp",
        };
        var previous = new Dictionary<string, string?>();
        try
        {
            foreach (var pair in values)
            {
                previous.Add(pair.Key, Environment.GetEnvironmentVariable(pair.Key));
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            AppEnvironment.GetValue(AppEnvironment.Variables.Gateway).ShouldBeNullOrEmpty();
            AppEnvironment.TryGetEndpoint(TcpServerSmoke.Endpoints[model], out _).ShouldBeTrue();

            // Act
            var failure = Should.Throw<ArgumentException>(() =>
            {
                using DatabaseEngine engine = TcpServerSmoke.Create(model, new Uri("tcp://127.0.0.1:5740"), configureListener: false).Engine;
            });

            // Assert
            failure.ParamName.ShouldBe("Listener");
            failure.Message.ShouldStartWith("CK08001:", Case.Sensitive);
        }
        finally
        {
            foreach (var pair in previous) { Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        }
    }
}
