using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentRegisteredFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: callback faults retain cause and receive a located coded diagnostic")]
    public async Task Execute_ExternalFault_ShouldPreserveCauseAndLocateCallAsync()
    {
        var cause = new InvalidOperationException("application failure detail");
        await using var engine = await BuildAsync(DocumentScalarFunction.Create<decimal, decimal>("fail_value", _ => throw cause));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        const string query = "-- callback\nSELECT fail_value(amount) FROM items";
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.InnerException.ShouldBeSameAs(cause);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05017");
        diagnostic.Message.ShouldBe(error.Message);
        diagnostic.Start.ShouldBe(query.IndexOf("fail_value", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(query.IndexOf(" FROM", StringComparison.Ordinal));
        diagnostic.Line.ShouldBe(2);
        error.Message.ShouldContain("fail_value", Case.Sensitive);
        error.Message.ShouldContain(cause.Message, Case.Sensitive);
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT COUNT(*) FROM items"))).ShouldHaveSingleItem().GetValue(0).ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: database failures retain identity while application IO and timeout failures are coded")]
    [InlineData("database")]
    [InlineData("io")]
    [InlineData("timeout")]
    public void Invoke_CallbackFailure_ShouldPreserveDatabaseOrCodeApplicationFault(string kind)
    {
        Exception cause = kind switch
        {
            "database" => new DatabaseException("application owned failure"),
            "io" => new IOException("storage read failed"),
            _ => new TimeoutException("operation timed out"),
        };
        var function = DocumentScalarFunction.Create<decimal, decimal>("owned_failure", _ => throw cause);
        var error = Should.Throw<Exception>(() => Invoke(function, [DocumentValue.FromDecimal(1)], CancellationToken.None));
        if (cause is DatabaseException) { error.ShouldBeSameAs(cause); }
        else { var coded = error.ShouldBeAssignableTo<DatabaseException>(); coded.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017"); coded.InnerException.ShouldBeSameAs(cause); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: statement cancellation is preserved while foreign cancellation is coded")]
    public void Invoke_Cancellation_ShouldFollowStatementToken()
    {
        using var statement = new CancellationTokenSource();
        var canceled = new OperationCanceledException(statement.Token);
        var function = DocumentScalarFunction.Create<decimal, decimal>("cancel_statement", _ => { statement.Cancel(); throw canceled; });
        Should.Throw<OperationCanceledException>(() => Invoke(function, [DocumentValue.FromDecimal(1)], statement.Token)).ShouldBeSameAs(canceled);
        var foreign = new OperationCanceledException(new CancellationToken(canceled: true));
        var unrelated = DocumentScalarFunction.Create<decimal, decimal>("foreign_cancel", _ => throw foreign);
        var error = Should.Throw<DatabaseException>(() => Invoke(unrelated, [DocumentValue.FromDecimal(1)], CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        error.InnerException.ShouldBeSameAs(foreign);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: invalid return values and reused group accumulators are coded")]
    public async Task Execute_InvalidFunctionContract_ShouldRefuseWithCodeAsync()
    {
        await using var engine = await BuildAsync(new DocumentWrongResultFunction(), new DocumentReusingAggregateFunction());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var wrong = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT wrong_result() FROM items", cancellationToken: CancellationToken.None));
        wrong.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        wrong.InnerException.ShouldBeOfType<InvalidCastException>();
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT reuse_group(amount) FROM items"))).ShouldHaveSingleItem().GetValue(0).ShouldBe(3L);
        var reused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT reuse_group(amount) FROM items", cancellationToken: CancellationToken.None));
        reused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        reused.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: native scalar and aggregate overflow keep arithmetic diagnostics")]
    [InlineData("POWER(10,100)")]
    [InlineData("SUM(amount * 6000000000000000000000000000)")]
    [InlineData("AVG(amount * 6000000000000000000000000000)")]
    public async Task Execute_NativeOverflow_ShouldKeepArithmeticCodeAsync(string expression)
    {
        await using var engine = await BuildAsync();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT " + expression + " FROM items", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02007");
        error.InnerException.ShouldBeOfType<OverflowException>();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: custom DML values stage atomically and INSERT SELECT uses the registered catalog")]
    public async Task Execute_CustomDml_ShouldRetainStatementAtomicityAsync()
    {
        await using var engine = await BuildAsync(Slug(),
            DocumentScalarFunction.Create<decimal, decimal>("raise_second", value => value == 4 ? throw new InvalidOperationException("second row refused") : value + 1));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE items SET amount = raise_second(amount)", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT amount FROM items ORDER BY amount"))).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 2m, 4m, 9m });
        await session.CreateCollectionAsync("copies", CancellationToken.None);
        (await session.ExecuteAsync("INSERT INTO copies (KEY k, VALUE v) SELECT slug(name) AS k, {n:slug(name)} AS v FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(3);
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT n FROM copies ORDER BY n"))).Select(row => row.GetString(0)).ShouldBe(["alpha", "beta", "gamma"]);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE items SET amount = raise_second(amount)", cancellationToken: CancellationToken.None));
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT amount FROM items ORDER BY amount"))).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 2m, 4m, 9m });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: context follows database identity and the explicit transaction clock")]
    public async Task Execute_CustomContext_ShouldKeepTransactionTimestampAsync()
    {
        await using var engine = await BuildAsync(new DocumentContextFunction());
        var database = await engine.CreateDatabaseAsync("context_database", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        var request = DocumentQueryRequest.FromOql("SELECT call_context() FROM items WHERE amount = 2");
        string first = (await RowsAsync(session, request)).ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<string>();
        string second = (await RowsAsync(session, request)).ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<string>();
        first.ShouldStartWith("context_database|", Case.Sensitive);
        first.Length.ShouldBeGreaterThan("context_database|".Length);
        second.ShouldBe(first);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static DocumentValue Invoke(DocumentScalarFunction function, DocumentValue[] values, CancellationToken cancellationToken)
    {
        var context = new DocumentFunctionContext(default, cancellationToken);
        var arguments = new DocumentArguments(values, in context);
        return function.Invoke(in arguments);
    }
}
