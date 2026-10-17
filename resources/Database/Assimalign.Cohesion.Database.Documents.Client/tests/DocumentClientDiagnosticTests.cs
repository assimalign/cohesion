using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentClientDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Rejections preserve carrier diagnostics and compose text once on both transports")]
    [InlineData(false, "SELECT *\nFROM items LIMIT 1", "COHDBL001", "LIMIT", ProtocolErrorCode.ParseFailure)]
    [InlineData(true, "SELECT *\nFROM items LIMIT 1", "COHDBL001", "LIMIT", ProtocolErrorCode.ParseFailure)]
    [InlineData(false, "SELECT *\nFROM absent", "COHDOC002", "SELECT *\nFROM absent", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "SELECT *\nFROM absent", "COHDOC002", "SELECT *\nFROM absent", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, "CREATE COLLECTION \" \"", "COHDOC001", "\" \"", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "CREATE COLLECTION \" \"", "COHDOC001", "\" \"", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, "DROP COLLECTION IF EXISTS \" \"", "COHDOC001", "\" \"", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "DROP COLLECTION IF EXISTS \" \"", "COHDOC001", "\" \"", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, "CREATE COLLECTION \"\"", "OQL0002", "\"\"", ProtocolErrorCode.ParseFailure)]
    [InlineData(true, "CREATE COLLECTION \"\"", "OQL0002", "\"\"", ProtocolErrorCode.ParseFailure)]
    [InlineData(false, "DROP COLLECTION IF EXISTS \"\"", "OQL0002", "\"\"", ProtocolErrorCode.ParseFailure)]
    [InlineData(true, "DROP COLLECTION IF EXISTS \"\"", "OQL0002", "\"\"", ProtocolErrorCode.ParseFailure)]
    [InlineData(false, "SELECT\n n / 0 FROM items", "COHDOC006", "n / 0", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "SELECT\n n / 0 FROM items", "COHDOC006", "n / 0", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, "SELECT\n $missing FROM items", "COHDOC005", "$missing", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "SELECT\n $missing FROM items", "COHDOC005", "$missing", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, "CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(NAME)", "COHDOC004", "CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(NAME)", ProtocolErrorCode.ExecutionFailure)]
    [InlineData(true, "CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(NAME)", "COHDOC004", "CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(NAME)", ProtocolErrorCode.ExecutionFailure)]
    public async Task QueryAsync_CodedRejection_ShouldPreserveDiagnosticsAndSession(bool tcp, string statement,
        string code, string span, ProtocolErrorCode classification)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
            await connection.CreateCollectionAsync("items", harness.Token);
            await connection.PutAsync("items", "one", "{\"n\":2}"u8.ToArray(), cancellationToken: harness.Token);
            await using var embedded = await harness.Database.CreateSessionAsync(harness.Token);
            var original = await Should.ThrowAsync<DatabaseException>(async () =>
                await embedded.ExecuteAsync(statement, cancellationToken: harness.Token));

            // Act
            var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
                await connection.QueryAsync(statement, cancellationToken: harness.Token));

            // Assert
            failure.Code.ShouldBe(classification);
            failure.Diagnostics.Select(Value).ShouldBe(original.Diagnostics.Select(Value));
            ((DatabaseException)failure).Diagnostics.ShouldBeSameAs(failure.Diagnostics);
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(code);
            diagnostic.Start.ShouldBe(statement.IndexOf(span, StringComparison.Ordinal));
            diagnostic.End.ShouldBe(diagnostic.Start + span.Length);
            diagnostic.Line.ShouldBe(1 + statement[..diagnostic.Start!.Value].Count(character => character == '\n'));
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            failure.Message.ShouldBe($"{code}: {diagnostic.Message}");
            connection.IsOpen.ShouldBeTrue();
            (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2m);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.GetCollectionsAsync(harness.Token)).ShouldBe(["items"]);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Multiple parser diagnostics retain every field and their existing code prefixes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_MultipleParseDiagnostics_ShouldPreserveCompleteCarrier(bool tcp)
    {
        // Arrange
        const string statement = "SELECT (SELECT a FROM nested) FROM items JOIN other ON items.a = other.a";
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await using var embedded = await harness.Database.CreateSessionAsync(harness.Token);
        var original = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await embedded.ExecuteAsync(statement, cancellationToken: harness.Token));

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        failure.Diagnostics.Count.ShouldBe(2);
        failure.Diagnostics.Select(Value).ShouldBe(original.Diagnostics.Select(Value));
        failure.Message.ShouldBe(string.Join("; ", failure.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Direct system collection writes preserve unlocated diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCollectionAsync_SystemCollection_ShouldPreserveUnlocatedCarrier(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.CreateCollectionAsync("COHESION_SCHEMA.OBJECT_OWNERSHIP", harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC004");
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
        failure.Message.ShouldBe($"COHDOC004: {diagnostic.Message}");
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
    }

    private static object Value(Diagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Severity, diagnostic.Location);
}
