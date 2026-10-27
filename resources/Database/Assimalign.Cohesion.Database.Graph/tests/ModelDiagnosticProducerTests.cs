using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class ModelDiagnosticProducerTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: overlapping operations retain their refusal and code")]
    public async Task BeginOperation_OverlappingOperation_ShouldCarryCodeAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        try
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await database.BeginOperationAsync(session, CancellationToken.None));
            Refusal(error, "CK07003", "Dispose the active graph operation before starting another operation on this session.");
        }
        finally { await operation.CompleteAsync(); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: a completed operation retains its refusal and code")]
    public async Task EnsureActive_CompletedOperation_ShouldCarryCodeAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        await operation.CompleteAsync();
        Refusal(Should.Throw<DatabaseException>(operation.EnsureActive), "CK04002", "The graph operation's transaction is no longer active.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: unsupported isolation retains its refusal and code")]
    public async Task BeginTransaction_UnsupportedIsolation_ShouldCarryCodeAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None));
        Refusal(error, "CK04001", "The graph engine supports Snapshot and ReadCommitted isolation.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: materialized row refusals carry shared identities")]
    public async Task Read_InvalidRowAccessor_ShouldCarryCodeAsync()
    {
        await using var result = new GraphQueryResult([], [new object?[] { 1, null }]);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            Refusal(Should.Throw<DatabaseException>(() => row.GetBytes(0)), "CK02001", "Field 0 is not JSON or a string.");
            Refusal(Should.Throw<DatabaseException>(() => row.GetInt64(1)), "CK02002", "Field 1 is null.");
        }
    }

    private static void Refusal(DatabaseException error, string code, string message)
    {
        error.ShouldBeOfType<DatabaseException>();
        Diagnostic(error, code, message);
    }

    private static void Diagnostic(DatabaseException error, string code, string message)
    {
        error.Message.ShouldBe(message);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(message);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
    }
}
