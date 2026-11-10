using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSortFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Sort: session disposal at the last retained key surfaces unwrapped (#1805)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_SessionDisposedAtSort_ShouldPreserveFailureAsync(bool window)
    {
        // Arrange: the scalar counts key evaluations; the retention hook closes the session
        // after the final key is owned and before the first comparison consumes work.
        int keys = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("sort-disposal");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("sort_key", value => { keys++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[3,2,1]");
        var parameters = new Dictionary<string, object?> { ["rows"] = input.RootElement };
        string text = window ? "SELECT RANK() OVER (ORDER BY sort_key(x)) AS r FROM @rows AS x" :
            "SELECT 0 AS n FROM [] AS y UNION ALL SELECT x AS n FROM @rows AS x ORDER BY sort_key(n)";
        var request = DocumentQueryRequest.FromOql(text);
        bool disposed = false;
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            memory.RetentionObserved = () =>
            {
                if (keys != 3 || disposed) { return; }
                disposed = true;
                memory.RetentionObserved = null;
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
            return await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None);
        }, CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK07004");
        disposed.ShouldBeTrue();
        keys.ShouldBe(3);
    }
}
