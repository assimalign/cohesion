using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentInsertNumericWireTests
{
    public static TheoryData<bool, string, string> LossyInputs
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (bool tcp in new[] { false, true })
            {
                foreach (string number in new[] { "1e-29", "1.00000000000000000000000000001", "1e29" })
                {
                    foreach (string source in new[] { "literal", "parameter", "nested", "select" })
                    { cases.Add(tcp, number, source); }
                }
            }
            return cases;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - INSERT: lossy numeric inputs fail atomically and preserve the wire session")]
    [MemberData(nameof(LossyInputs))]
    public async Task ExecuteAsync_LossyNumber_ShouldPreserveCollectionAndSession(bool tcp, string number, string source)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        var original = await connection.PutAsync("items", "seed", "{\"n\":2}"u8.ToArray(), cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        using var json = JsonDocument.Parse(source == "nested" ? "{\"n\":[" + number + "]}" : number);
        string operand = source is "parameter" or "nested" ? "$n" : number;
        string statement = source == "select"
            ? $"INSERT INTO items (KEY k, VALUE v) SELECT 'bad' AS k, {operand} + n AS v FROM items"
            : $"INSERT INTO items (KEY, VALUE) VALUES ('first', 1), ('bad', {operand})";

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.ExecuteAsync(statement,
            new Dictionary<string, object?> { ["n"] = json.RootElement }, harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD02007");
        diagnostic.Start.ShouldBe(statement.IndexOf(operand, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + operand.Length);
        failure.Message.ShouldBe($"CD02007: {diagnostic.Message}");
        var saved = (await connection.GetAsync("items", "seed", harness.Token)).ShouldNotBeNull();
        saved.Version.ShouldBe(original.Version);
        saved.Content.ToArray().ShouldBe(original.Content.ToArray());
        (await connection.QueryAsync("SELECT META().id FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("seed");
        (await connection.GetAsync("items", "first", harness.Token)).ShouldBeNull();
        (await connection.GetAsync("items", "bad", harness.Token)).ShouldBeNull();
        var put = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.PutAsync("items", "seed", Encoding.UTF8.GetBytes(number), cancellationToken: harness.Token));
        put.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        (await connection.GetAsync("items", "seed", harness.Token)).ShouldNotBeNull().Version.ShouldBe(original.Version);
        (await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('next', 1)", cancellationToken: harness.Token)).ShouldBe(1);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
