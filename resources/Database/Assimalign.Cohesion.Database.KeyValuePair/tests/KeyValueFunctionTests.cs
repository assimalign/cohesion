using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueFunctionTests
{
    public static IEnumerable<object?[]> Primitives()
    {
        yield return [DatabaseType.Null, null];
        yield return [DatabaseType.Boolean, true];
        yield return [DatabaseType.Int8, (sbyte)-3];
        yield return [DatabaseType.Int16, (short)17];
        yield return [DatabaseType.Int32, 23];
        yield return [DatabaseType.Int64, 29L];
        yield return [DatabaseType.Float32, 1.25f];
        yield return [DatabaseType.Float64, 2.5d];
        yield return [DatabaseType.Decimal, 3.75m];
        yield return [DatabaseType.String, "hello"];
        yield return [DatabaseType.Binary, new byte[] { 1, 2, 3 }];
        yield return [DatabaseType.Date, new DateOnly(2026, 10, 24)];
        yield return [DatabaseType.Time, new TimeOnly(12, 34, 56)];
        yield return [DatabaseType.DateTime, new DateTime(2026, 10, 24, 12, 34, 56, DateTimeKind.Unspecified)];
        yield return [DatabaseType.DateTimeOffset, new DateTimeOffset(2026, 10, 24, 12, 34, 56, TimeSpan.FromHours(2))];
        yield return [DatabaseType.TimeSpan, TimeSpan.FromSeconds(17)];
        yield return [DatabaseType.Guid, Guid.Parse("c7c9456a-491b-4c65-bafd-d4993e2d7caf")];
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: all native primitives execute through typed and CALL requests")]
    [MemberData(nameof(Primitives))]
    public async Task ExecuteAsync_Primitive_ShouldRoundTripAsync(DatabaseType type, object? value)
    {
        var builder = KeyValueDatabaseEngine.CreateBuilder("functions");
        var function = new EchoKeyValueFunction("echo", type);
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("ECHO", [value]), CancellationToken.None))).ShouldBe(value);
        (await ValueAsync(await session.ExecuteCommandTextAsync("call eChO @arg",
            new Dictionary<string, object?> { ["arg"] = value }, cancellationToken: CancellationToken.None))).ShouldBe(value);
        function.Calls.ShouldBe(2);
        function.Database.ShouldBe(database.Name);
        engine.Functions.GetOverloads("echo").ShouldHaveSingleItem().ShouldBeSameAs(function);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: compiled delegates cover arities zero through four and manual functions have arbitrary fixed arity")]
    public async Task ExecuteAsync_TypedAndManual_ShouldUseOneInvocationPathAsync()
    {
        int calls = 0;
        var builder = KeyValueDatabaseEngine.CreateBuilder("typed-functions");
        builder.Functions.Add(KeyValueFunction.Create("f0", () => { calls++; return 10L; }));
        builder.Functions.Add(KeyValueFunction.Create<long, long>("f1", a => { calls++; return a; }));
        builder.Functions.Add(KeyValueFunction.Create<long, long, long>("f2", (a, b) => { calls++; return a + b; }));
        builder.Functions.Add(KeyValueFunction.Create<long, long, long, long>("f3", (a, b, c) => { calls++; return a + b + c; }));
        builder.Functions.Add(KeyValueFunction.Create<long, long, long, long, long>("f4", (a, b, c, d) => { calls++; return a + b + c + d; }));
        builder.Functions.Add(new FiveArgumentKeyValueFunction());
        builder.AddDatabase("first").AddDatabase("second");
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        engine.Functions.Count.ShouldBe(6);
        foreach (string name in new[] { "first", "second" })
        {
            var database = await engine.OpenDatabaseAsync(name, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            for (int count = 0; count <= 5; count++)
            {
                object?[] values = new object?[count];
                Array.Fill(values, 2L);
                (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("f" + count, values), CancellationToken.None)))
                    .ShouldBe(count == 0 ? 10L : count * 2L);
            }
        }
        calls.ShouldBe(10);
        await Should.ThrowAsync<InvalidOperationException>(async () => await builder.BuildAsync(CancellationToken.None));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(KeyValueFunction.Create("late", () => 1L)));
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: resolution preserves exact types and rejects NULL ambiguity")]
    [InlineData("arity", "CV01019")]
    [InlineData("types", "CV01020")]
    [InlineData("null", "CV01021")]
    public async Task ExecuteAsync_InvalidOverload_ShouldReportCodeAsync(string scenario, string code)
    {
        int calls = 0;
        var builder = KeyValueDatabaseEngine.CreateBuilder("overloads");
        builder.Functions.Add(KeyValueFunction.Create<long, long>("f", value => { calls++; return value; }));
        builder.Functions.Add(KeyValueFunction.Create<string, string>("f", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        object?[] arguments = scenario switch { "arity" => [], "types" => [1], _ => [null] };
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(new KeyValueFunctionRequest("f", arguments), CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        calls.ShouldBe(0);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("f", [4L]), CancellationToken.None))).ShouldBe(4L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: strict NULL skips the body and called-on-NULL invokes nullable delegates")]
    public async Task ExecuteAsync_NullPolicies_ShouldBeExplicitAsync()
    {
        int calls = 0;
        var builder = KeyValueDatabaseEngine.CreateBuilder("null-functions");
        builder.Functions.Add(KeyValueFunction.Create<long, long>("strict", value => { calls++; return value; }));
        builder.Functions.Add(KeyValueFunction.Create<long?, long?>("called", value => { calls++; return value ?? 91; }, KeyValueNullBehavior.CalledOnNullInput));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("strict", [null]), CancellationToken.None))).ShouldBeNull();
        calls.ShouldBe(0);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("called", [null]), CancellationToken.None))).ShouldBe(91L);
        calls.ShouldBe(1);
        Should.Throw<ArgumentException>(() => KeyValueFunction.Create<long, long>("bad", value => value, KeyValueNullBehavior.CalledOnNullInput));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: binary ownership survives caller and typed callback mutation")]
    public async Task ExecuteAsync_Binary_ShouldDetachArgumentsAndResultsAsync()
    {
        byte[] source = [1, 2];
        var request = new KeyValueFunctionRequest("binary", [source]);
        source[0] = 99;
        ((ReadOnlyMemory<byte>)request.Arguments[0]!).ToArray().ShouldBe(new byte[] { 1, 2 });
        var builder = KeyValueDatabaseEngine.CreateBuilder("binary-functions");
        byte[]? callbackBuffer = null;
        builder.Functions.Add(KeyValueFunction.Create<byte[], byte[]>("binary", bytes =>
        {
            bytes[0]++;
            callbackBuffer = bytes;
            return bytes;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        byte[] first = (await ValueAsync(await session.ExecuteAsync(request, CancellationToken.None))).ShouldBeOfType<byte[]>();
        first.ShouldBe(new byte[] { 2, 2 });
        callbackBuffer![0] = 50;
        first.ShouldBe(new byte[] { 2, 2 });
        first[0] = 70;
        (await ValueAsync(await session.ExecuteAsync(request, CancellationToken.None))).ShouldBe(new byte[] { 2, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: ordinary callback failure retains its cause and the explicit transaction remains usable")]
    public async Task ExecuteAsync_CallbackFault_ShouldRetainCauseAndSessionAsync()
    {
        var cause = new InvalidOperationException("application body fault");
        var builder = KeyValueDatabaseEngine.CreateBuilder("fault-functions");
        builder.Functions.Add(new ConstantKeyValueFunction("fault", DatabaseType.Int64, () => throw cause));
        builder.Functions.Add(KeyValueFunction.Create("ok", () => 42L));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new KeyValueFunctionRequest("fault", []), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV05003");
        error.InnerException.ShouldBeSameAs(cause);
        transaction.State.ShouldBe(TransactionState.Active);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("ok", []), CancellationToken.None))).ShouldBe(42L);
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: declared result types are validated before delivery")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_InvalidResult_ShouldReportDeclaredTypeAsync(bool unsupported)
    {
        var builder = KeyValueDatabaseEngine.CreateBuilder("result-functions");
        builder.Functions.Add(new ConstantKeyValueFunction("wrong", DatabaseType.Int64, () => unsupported ? new object() : 1));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new KeyValueFunctionRequest("wrong", []), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV02016");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: database and statement cancellation retain identity; application faults are coded")]
    public void Invoke_CallbackFailures_ShouldPreserveDatabaseOrCancellationAndCodeApplicationFaults()
    {
        Exception[] failures = [new DatabaseException("original"), new StorageCorruptionException("storage"), new IOException("device"), new TimeoutException("timeout")];
        foreach (Exception failure in failures)
        {
            var function = new ConstantKeyValueFunction("fault", DatabaseType.Int64, () => throw failure);
            var arguments = new KeyValueArguments([]);
            try { function.Invoke(in arguments); }
            catch (Exception actual)
            {
                if (failure is DatabaseException) { actual.ShouldBeSameAs(failure); }
                else { var coded = actual.ShouldBeOfType<DatabaseException>(); coded.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV05003"); coded.InnerException.ShouldBeSameAs(failure); }
                continue;
            }
            false.ShouldBeTrue("The callback failure was not propagated.");
        }
        using var cancellation = new CancellationTokenSource();
        var canceled = new OperationCanceledException(cancellation.Token);
        var cancelFunction = new ConstantKeyValueFunction("cancel", DatabaseType.Int64, () => { cancellation.Cancel(); throw canceled; });
        var cancelArguments = new KeyValueArguments([], new KeyValueFunctionContext(default, cancellation.Token));
        try { cancelFunction.Invoke(in cancelArguments); }
        catch (OperationCanceledException actual) { actual.ShouldBeSameAs(canceled); return; }
        false.ShouldBeTrue("The actual cancellation was not propagated.");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: unrelated cancellation is a coded callback fault")]
    public void Invoke_UnrequestedCancellation_ShouldRetainInnerCause()
    {
        var cause = new OperationCanceledException();
        var function = new ConstantKeyValueFunction("fault", DatabaseType.Int64, () => throw cause);
        var arguments = new KeyValueArguments([]);
        try { function.Invoke(in arguments); }
        catch (DatabaseException error)
        {
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV05003");
            error.InnerException.ShouldBeSameAs(cause);
            return;
        }
        false.ShouldBeTrue("The unrelated cancellation was not classified.");
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: command names are reserved")]
    [InlineData("GET")]
    [InlineData("put")]
    [InlineData("Delete")]
    [InlineData("EXISTS")]
    [InlineData("SCAN")]
    [InlineData("KEYSPACES")]
    [InlineData("CALL")]
    public void Add_ReservedName_ShouldReject(string name)
    {
        var builder = KeyValueDatabaseEngine.CreateBuilder("reserved");
        Should.Throw<ArgumentException>(() => builder.Functions.Add(KeyValueFunction.Create(name, () => 1L)));
        builder.Functions.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: metadata/default names/collisions are validated before storage creation")]
    public async Task BuildAsync_InvalidMetadata_ShouldLeaveNoFilesAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "cohesion-kv-functions-" + Guid.NewGuid().ToString("N"));
        var builder = KeyValueDatabaseEngine.CreateBuilder("validation");
        builder.Options.RootPath = path;
        builder.Options.BufferPoolCapacity = 1;
        builder.Functions.Add(KeyValueFunction.Create<long, long>("same", value => value));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(KeyValueFunction.Create<long, long>("SAME", value => value + 1)));
        Should.Throw<ArgumentException>(() => KeyValueFunction.Create(default, () => 1L));
        Should.Throw<ArgumentException>(() => new KeyValueFunctionRequest(default, []));
        Should.Throw<ArgumentException>(() => builder.Functions.Contains(default));
        Should.Throw<NotSupportedException>(() => KeyValueFunction.Create<object>("unsupported", () => new object()));
        Should.Throw<ArgumentOutOfRangeException>(() => new EchoKeyValueFunction("invalid", DatabaseType.Json));
        Should.Throw<ArgumentException>(() => new KeyValueFunctionRequest("same", [new object()]));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await builder.BuildAsync(CancellationToken.None));
        Directory.Exists(path).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(KeyValueFunction.Create("late", () => 1L)));
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: CALL has parameter-only grammar and retains parse error ownership")]
    [InlineData("CALL", "CV00019")]
    [InlineData("CALL echo 42", "CV00016")]
    [InlineData("CALL echo @missing", "CV01017")]
    [InlineData("CALL bad-name", "CV00004")]
    public async Task ExecuteCommandTextAsync_InvalidCall_ShouldBeParseFailureAsync(string command, string code)
    {
        await using var engine = KeyValueDatabaseEngine.Create("grammar", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteCommandTextAsync(command, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: actual callback cancellation preserves its instance and leaves an explicit transaction usable")]
    public async Task ExecuteAsync_CallbackCancellation_ShouldPreserveIdentityAndReuseAsync()
    {
        // Arrange: cancellation occurs inside the admitted callback, with no scheduling or timing dependency.
        using var cancellation = new CancellationTokenSource();
        var cause = new OperationCanceledException(cancellation.Token);
        var builder = KeyValueDatabaseEngine.CreateBuilder("cancellation-functions");
        builder.Functions.Add(new ConstantKeyValueFunction("cancel", DatabaseType.Int64, () =>
        {
            cancellation.Cancel();
            throw cause;
        }));
        builder.Functions.Add(KeyValueFunction.Create("ok", () => 42L));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act: await the ValueTask directly; an assertion helper's Task adapter can synthesize cancellation.
        OperationCanceledException? actual = null;
        try
        {
            await session.ExecuteAsync(new KeyValueFunctionRequest("cancel", []), cancellation.Token);
        }
        catch (OperationCanceledException error)
        {
            actual = error;
        }

        // Assert: the next statement uses a fresh token on the same admitted transaction/session.
        actual.ShouldNotBeNull();
        actual.ShouldBeSameAs(cause);
        actual.CancellationToken.ShouldBe(cancellation.Token);
        transaction.State.ShouldBe(TransactionState.Active);
        (await ValueAsync(await session.ExecuteAsync(new KeyValueFunctionRequest("ok", []), CancellationToken.None))).ShouldBe(42L);
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: direct argument reads enforce exact primitive and NULL contracts")]
    public void Get_NullOrWrongPrimitive_ShouldThrowDeclaredCastFailure()
    {
        // Arrange: ref-like arguments cannot be captured by an assertion delegate.
        var arguments = new KeyValueArguments([null, 17L]);
        Exception? nullFailure = null;
        Exception? typeFailure = null;
        Exception? unsupportedFailure = null;

        // Act
        try { arguments.Get<int>(0); } catch (Exception error) { nullFailure = error; }
        try { arguments.Get<int>(1); } catch (Exception error) { typeFailure = error; }
        try { arguments.Get<object>(1); } catch (Exception error) { unsupportedFailure = error; }

        // Assert
        nullFailure.ShouldBeOfType<InvalidCastException>();
        typeFailure.ShouldBeOfType<InvalidCastException>();
        unsupportedFailure.ShouldBeOfType<NotSupportedException>();
        arguments.Get<long?>(0).ShouldBeNull();
        arguments.Get<long>(1).ShouldBe(17L);
    }

    private static async Task<object?> ValueAsync(DatabaseResult result)
    {
        var set = result.ShouldBeAssignableTo<DatabaseResultSet>();
        await using (set)
        {
            set.Columns.ShouldHaveSingleItem().Name.ShouldBe("value");
            set.Columns[0].IsNullable.ShouldBeTrue();
            set.AffectedCount.ShouldBe(-1);
            set.Diagnostics.ShouldBeEmpty();
            await using var rows = set.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator();
            (await rows.MoveNextAsync()).ShouldBeTrue();
            rows.Current.FieldCount.ShouldBe(1);
            object? value = rows.Current.GetValue(0);
            (await rows.MoveNextAsync()).ShouldBeFalse();
            return value;
        }
    }

}
