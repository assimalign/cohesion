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
    [InlineData("", "CV00001")]
    [InlineData("PUT KEYSPACES @v", "CV03002")]
    [InlineData("NOPE", "CV00003")]
    [InlineData("GET @empty", "CV00004")]
    [InlineData("PUT", "CV00005")]
    [InlineData("PUT @k @v WHEN ABSENT", "CV00006")]
    [InlineData("DELETE", "CV00007")]
    [InlineData("DELETE @k WHEN @etag", "CV00008")]
    [InlineData("SCAN FROM", "CV00009")]
    [InlineData("SCAN FROM @k FROM @k", "CV00010")]
    [InlineData("SCAN NOPE @k", "CV00011")]
    [InlineData("GET @wrong", "CV02012")]
    [InlineData("PUT @k @v IF @wrong", "CV02013")]
    [InlineData("SCAN LIMIT -1", "CV02014")]
    [InlineData("SCAN LIMIT @wrong", "CV02015")]
    [InlineData("GET literal", "CV00016")]
    [InlineData("GET @missing", "CV01017")]
    [InlineData("GET", "CV00018")]
    public async Task ExecuteAsync_ParserRefusal_ShouldReceiveCodeAndReuseSession(string statement, string code)
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new() { Database = "kv", Principal = "tester", EndPoint = harness.Listener.EndPoint },
            Family = KeyValueProtocol.Family, ConnectionFactory = harness.Factory,
        });
        await using var connection = await client.RentAsync(KeyValueClientTestHarness.Timeout());
        var parameters = new Dictionary<string,object?> { ["k"] = new byte[]{1}, ["v"] = new byte[]{2}, ["etag"] = 1L, ["empty"] = Array.Empty<byte>(), ["wrong"] = "wrong" };
        // Act
        var error = await Should.ThrowAsync<DatabaseClientException>(async () => await connection.ExecuteAsync(new KeyValueExecuteExchange(statement, parameters), KeyValueClientTestHarness.Timeout()));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldStartWith(code + ": ", Case.Sensitive);
        error.Diagnostics.ShouldBeEmpty();
        connection.ServerVersion.ShouldBe(ProtocolVersion.Current);
        connection.Capabilities.ShouldBeEmpty();
        await connection.ExecuteAsync(new KeyValueExecuteExchange("KEYSPACES", null), KeyValueClientTestHarness.Timeout());
        harness.Server.Sessions.Count.ShouldBe(1);
    }
}
