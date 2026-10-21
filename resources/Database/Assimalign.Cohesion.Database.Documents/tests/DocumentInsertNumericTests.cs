using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentInsertNumericTests
{
    public static TheoryData<string, string, bool> LossyInputs
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (string number in new[] { "1e-29", "1.00000000000000000000000000001", "1e29" })
            {
                foreach (string source in new[] { "literal", "parameter", "nested", "select-literal", "select-parameter", "grouped", "empty", "empty-parameter" })
                {
                    foreach (bool transaction in new[] { false, true })
                    { cases.Add(number, source, transaction); }
                }
            }
            return cases;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - INSERT: lossy numbers fail with COHDOC007 and roll back all work")]
    [MemberData(nameof(LossyInputs))]
    public async Task Execute_LossyNumber_ShouldPreserveCollectionAsync(string number, string source, bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("numeric", new());
        var database = await engine.CreateDatabaseAsync("numeric", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        var original = await items.PutAsync(session, "seed", "{\"n\":2}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_n ON items (n)", cancellationToken: CancellationToken.None);
        using var json = JsonDocument.Parse(source == "nested" ? "{\"n\":[" + number + "]}" : number);
        var parameters = new Dictionary<string, object?> { ["n"] = json.RootElement };
        string operand = source is "parameter" or "nested" or "select-parameter" or "empty-parameter" ? "$n" : number;
        string statement = source switch
        {
            "select-literal" or "select-parameter" => $"INSERT INTO items (KEY k, VALUE v) SELECT 'bad' AS k, {operand} + n AS v FROM items",
            "grouped" => $"INSERT INTO items (KEY k, VALUE v) SELECT 'bad' AS k, {operand} AS v FROM items GROUP BY {operand}",
            "empty" or "empty-parameter" => $"INSERT INTO items (KEY k, VALUE v) SELECT 'bad' AS k, {operand} + n AS v FROM items WHERE FALSE",
            _ => $"INSERT INTO items (KEY, VALUE) VALUES ('first', 1), ('bad', {operand})",
        };
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null)
        { await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', 1)", cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, parameters, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC007");
        diagnostic.Start.ShouldBe(statement.IndexOf(operand, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + operand.Length);
        diagnostic.Line.ShouldBe(1);
        failure.InnerException.ShouldBeOfType<JsonException>();
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            var refused = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None));
            refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBD001");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT META().id FROM items WHERE n = 2", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe("seed"); count++; }
        count.ShouldBe(1);
        var saved = (await items.GetAsync(session, "seed", CancellationToken.None)).ShouldNotBeNull();
        saved.Version.ShouldBe(original.Version);
        saved.Content.ToArray().ShouldBe(original.Content.ToArray());
        foreach (string id in new[] { "first", "bad", "prior" })
        { (await items.GetAsync(session, id, CancellationToken.None)).ShouldBeNull(); }
        (await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('next', 1)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - PutAsync: storage already rejects underflow, rounding and overflow without mutation")]
    [InlineData("1e-29", false)]
    [InlineData("1.00000000000000000000000000001", false)]
    [InlineData("1e29", false)]
    [InlineData("1e-29", true)]
    [InlineData("1.00000000000000000000000000001", true)]
    [InlineData("1e29", true)]
    public async Task PutAsync_LossyNumber_ShouldPreserveOriginalAsync(string number, bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("numeric", new());
        var database = await engine.CreateDatabaseAsync("numeric", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        var original = await items.PutAsync(session, "seed", "2"u8.ToArray(), cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null)
        { await items.PutAsync(session, "prior", "1"u8.ToArray(), cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<JsonException>(async () =>
            await items.PutAsync(session, "seed", Encoding.UTF8.GetBytes(number), cancellationToken: CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("exactly representable", Case.Sensitive);
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        var saved = (await items.GetAsync(session, "seed", CancellationToken.None)).ShouldNotBeNull();
        saved.Version.ShouldBe(original.Version);
        saved.Content.ToArray().ShouldBe(original.Content.ToArray());
        (await items.GetAsync(session, "prior", CancellationToken.None)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - INSERT: exactly representable numbers retain their value")]
    [InlineData("1e-28")]
    [InlineData("1.00000000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0e-100")]
    [InlineData("100000000000000000000000000000e-1")]
    public async Task Execute_ExactNumber_ShouldStoreWithoutLossAsync(string number)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("numeric", new());
        var database = await engine.CreateDatabaseAsync("numeric", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        using var expected = JsonDocument.Parse(number);

        // Act
        await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('literal', {number}), ('parameter', $n)",
            new Dictionary<string, object?> { ["n"] = expected.RootElement }, CancellationToken.None);

        // Assert
        foreach (string id in new[] { "literal", "parameter" })
        {
            using var actual = JsonDocument.Parse((await items.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull().Content);
            JsonElement.DeepEquals(expected.RootElement, actual.RootElement).ShouldBeTrue();
        }
    }
}
