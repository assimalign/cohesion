using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class ModelDiagnosticProducerTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Diagnostics: unsupported isolation retains its refusal and code")]
    public async Task BeginTransaction_UnsupportedIsolation_ShouldCarryCodeAsync()
    {
        await using var engine = KeyValueDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None));
        Refusal(error, "CK04001", "IsolationLevel.Serializable is not supported by the key-value engine yet: serialization-conflict detection is a post-MVP feature, and the session contract forbids running weaker than requested. Use IsolationLevel.Snapshot or IsolationLevel.ReadCommitted.");
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Diagnostics: entry format refusal carries its own identity")]
    public async Task Open_UnsupportedEntryFormat_ShouldCarryCodeAsync()
    {
        await using var engine = KeyValueDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await database.Catalog.SetEntrySpaceFormatVersionAsync(99, CancellationToken.None);
        await database.DisposeAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("db", CancellationToken.None));
        error.ShouldBeOfType<DatabaseException>();
        error.Message.ShouldContain("uses entry-space format 99, but this engine supports only format 2", Case.Sensitive);
        Diagnostic(error, "CV06107", error.Message);
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Diagnostics: cross-database sessions retain their refusal")]
    public async Task Get_ForeignSession_ShouldCarryCodeAsync()
    {
        await using var engine = KeyValueDatabaseEngine.Create("diagnostics", new());
        var first = await engine.CreateDatabaseAsync("first", CancellationToken.None);
        var second = await engine.CreateDatabaseAsync("second", CancellationToken.None);
        await using var session = await second.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await first.GetAsync(session, "key"u8.ToArray(), CancellationToken.None));
        Refusal(error, "CV07001", "The session does not belong to this key-value database.");
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Diagnostics: materialized row refusals carry shared identities")]
    public void Read_InvalidRowAccessor_ShouldCarryCode()
    {
        var row = new KeyValueMaterializedRow([1, null]);
        Refusal(Should.Throw<DatabaseException>(() => row.GetBytes(0)), "CK02001", "Field 0 is not binary.");
        Refusal(Should.Throw<DatabaseException>(() => row.GetInt64(1)), "CK02002", "Field 1 is NULL.");
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Diagnostics: an unsupported request retains its execution refusal")]
    public async Task Execute_UnknownRequest_ShouldCarryCodeAsync()
    {
        await using var engine = KeyValueDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        Refusal(await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new UnknownRequest(), CancellationToken.None)),
            "CV05002", "The key-value engine has no executor for UnknownRequest.");
    }

    private sealed class UnknownRequest : KeyValueRequest
    {
        internal UnknownRequest() : base(KeyValueOperation.Get) { }
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
