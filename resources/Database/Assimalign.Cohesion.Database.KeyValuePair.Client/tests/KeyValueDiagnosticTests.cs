using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

public sealed class KeyValueDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - every command parser refusal has a stable wire code")]
    [InlineData("", 1)]
    [InlineData("PUT KEYSPACES @v", 2)]
    [InlineData("NOPE", 3)]
    [InlineData("GET @empty", 4)]
    [InlineData("PUT", 5)]
    [InlineData("PUT @k @v WHEN ABSENT", 6)]
    [InlineData("DELETE", 7)]
    [InlineData("DELETE @k WHEN @etag", 8)]
    [InlineData("SCAN FROM", 9)]
    [InlineData("SCAN FROM @k FROM @k", 10)]
    [InlineData("SCAN NOPE @k", 11)]
    [InlineData("GET @wrong", 12)]
    [InlineData("PUT @k @v IF @wrong", 13)]
    [InlineData("SCAN LIMIT -1", 14)]
    [InlineData("SCAN LIMIT @wrong", 15)]
    [InlineData("GET literal", 16)]
    [InlineData("GET @missing", 17)]
    [InlineData("GET", 18)]
    public async Task ExecuteAsync_ParserRefusal_ShouldReceiveCodeAndReuseSession(string statement, int code)
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new() { Database = "kv", Principal = "tester", EndPoint = harness.Listener.EndPoint },
            Family = KeyValueProtocol.Family, ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.RentAsync(KeyValueClientTestHarness.Timeout());
        var parameters = new Dictionary<string,object?> { ["k"] = new byte[]{1}, ["v"] = new byte[]{2}, ["etag"] = 1L, ["empty"] = Array.Empty<byte>(), ["wrong"] = "wrong" };
        // Act
        var error = await Should.ThrowAsync<DatabaseClientException>(async () => await connection.ExecuteAsync(new KeyValueExecuteExchange(statement, parameters), KeyValueClientTestHarness.Timeout()));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldStartWith($"COHKV{code:000}: ");
        error.Diagnostics.ShouldBeEmpty();
        connection.ServerVersion.ShouldBe(ProtocolVersion.Current);
        connection.Capabilities.ShouldBeEmpty();
        await connection.ExecuteAsync(new KeyValueExecuteExchange("KEYSPACES", null), KeyValueClientTestHarness.Timeout());
        harness.Server.Sessions.Count.ShouldBe(1);
    }
}
