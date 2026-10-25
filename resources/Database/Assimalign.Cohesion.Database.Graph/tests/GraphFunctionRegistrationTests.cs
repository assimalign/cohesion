using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Exercises engine-owned function registration through ordinary Graph requests.</summary>
public sealed class GraphFunctionRegistrationTests
{
    /// <summary>Leaves custom call resolution to the engine instead of refusing it during request parsing.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: custom calls reach engine resolution through FromGql")]
    public void FromGql_CustomFunctionCall_ShouldAdmitRequestForEngineResolution()
    {
        // Arrange
        const string source = "RETURN developer_echo(42) AS echoed";

        // Act
        GraphQueryRequest request = Should.NotThrow(() => GraphQueryRequest.FromGql(source));

        // Assert
        request.Statement.Diagnostics.ShouldBeEmpty();
        request.Statement.GqlExpression.Projections.ShouldHaveSingleItem().Alias.ShouldBe("echoed");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Build Functions Should Freeze And Remain Engine Local")]
    public async Task Build_Functions_ShouldFreezeAndRemainEngineLocalAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("custom");
        var function = GraphScalarFunction.Create<long, long>("developer_echo", value => value);
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync();
        engine.Functions.Find("DEVELOPER_ECHO").ShouldBeSameAs(function);
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(GraphScalarFunction.Create<long, long>("late", value => value)));
        await using var standard = GraphDatabaseEngine.Create("standard", new());
        standard.Functions.Find("developer_echo").ShouldBeNull();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "RETURN developer_echo(42)")).ShouldHaveSingleItem().ShouldBe(new object?[] { 42L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: Register Native And Grammar Names Should Remain Reserved")]
    [InlineData("upper")]
    [InlineData("COUNT")]
    [InlineData("elementId")]
    [InlineData("relationships")]
    [InlineData("coalesce")]
    [InlineData("ORDER")]
    [InlineData("LIMIT")]
    [InlineData("LEFT")]
    [InlineData("OVER")]
    [InlineData("ALL")]
    public void Register_NativeAndGrammarNames_ShouldRemainReserved(string name)
    {
        var builder = GraphDatabaseEngine.CreateBuilder("reserved");
        Should.Throw<ArgumentException>(() => builder.Functions.Add(GraphScalarFunction.Create<long, long>(name, value => value)));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Register Duplicate Names And Default Names Should Be Refused")]
    public void Register_DuplicateNamesAndDefaultNames_ShouldBeRefused()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("duplicate");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_echo", value => value));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(GraphScalarFunction.Create<long, long>("DEVELOPER_ECHO", value => value)));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<long, long>(default, value => value));
        GraphFunctionName.IsValid("bad-name", out _).ShouldBeFalse();
        GraphFunctionName.IsValid("Δ_echo42", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Parser Standalone Unknown Name Should Retain Diagnostic")]
    public void Parser_StandaloneUnknownName_ShouldRetainDiagnostic()
    {
        var statement = new GqlQueryParser().Parse("RETURN developer_echo(42)");
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CG00010");
        var deferred = new GqlQueryParser(allowCustomFunctions: true).Parse("RETURN developer_echo(42)");
        deferred.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Custom Calls Should Work Across Clauses And Typed Ast")]
    public async Task Execute_CustomCalls_ShouldWorkAcrossClausesAndTypedAstAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("clauses");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_echo", value => value));
        builder.Functions.Add(GraphScalarFunction.Create<long, long, long>("developer_add", (left, right) => left + right));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "LET v=developer_echo(2) FOR x IN [1,2] FILTER developer_echo(x)=v WITH developer_add(v,x) AS n RETURN developer_echo(n) ORDER BY developer_echo(n)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 4L });
        await session.ExecuteAsync("INSERT (:Custom {n:developer_echo(7)})", cancellationToken: CancellationToken.None);
        (await Rows(session, "MATCH (n:Custom) WHERE developer_echo(n.n)=7 RETURN developer_add(n.n,1)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 8L });
        var request = GraphQueryRequest.FromGql("RETURN developer_echo(42)");
        request.Statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlNamedFunctionCallExpression>();
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(42L); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Null Contract Should Be Eager But Skip Strict Callback")]
    public async Task Execute_NullContract_ShouldBeEagerButSkipStrictCallbackAsync()
    {
        int strictCalls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("nulls");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_strict", value => { strictCalls++; return value; }));
        builder.Functions.Add(GraphScalarFunction.Create<string?, string>("developer_null", value => value ?? "missing", GraphNullBehavior.CalledOnNullInput));
        builder.Functions.Add(GraphScalarFunction.Create<long, long, long>("developer_pair", (left, right) => left + right));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "RETURN developer_strict(NULL), developer_null(NULL)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { null, "missing" });
        strictCalls.ShouldBe(0);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN developer_pair(NULL,1/0)"));
        error.Diagnostics[0].Code.ShouldBe("CG02013");
        (await Rows(session, "RETURN CASE WHEN TRUE THEN 1 ELSE developer_pair(NULL,1/0) END, coalesce(1,developer_pair(NULL,1/0))"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 1L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Callback Failures Should Preserve Causes And Database Identity")]
    public async Task Execute_CallbackFailures_ShouldPreserveCausesAndDatabaseIdentityAsync()
    {
        var failure = new InvalidOperationException("sentinel");
        var databaseFailure = new DatabaseException("database sentinel", []);
        var builder = GraphDatabaseEngine.CreateBuilder("errors");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_fail", _ => throw failure));
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_database_fail", _ => throw databaseFailure));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN developer_fail(1)"));
        error.InnerException.ShouldBeSameAs(failure);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        error.Diagnostics[0].Message.ShouldNotStartWith("CG05021", Case.Sensitive);
        error.Diagnostics[0].Start.ShouldNotBeNull();
        error.Diagnostics[0].End!.Value.ShouldBeLessThanOrEqualTo("RETURN developer_fail(1)".Length);
        var same = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN developer_database_fail(1)"));
        same.ShouldBeSameAs(databaseFailure);
        (await Rows(session, "RETURN upper('ok'), count(*)")).ShouldHaveSingleItem().ShouldBe(new object?[] { "OK", 1L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Canceled Callback Should Preserve Cancellation Identity")]
    public async Task Execute_CanceledCallback_ShouldPreserveCancellationIdentityAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var builder = GraphDatabaseEngine.CreateBuilder("cancel");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_cancel", _ => { cancellation.Cancel(); throw failure; }));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        OperationCanceledException? caught = null;
        try { await session.ExecuteAsync("RETURN developer_cancel(1)", cancellationToken: cancellation.Token); }
        catch (OperationCanceledException error) { caught = error; }
        caught.ShouldBeSameAs(failure);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        (await Rows(session, "RETURN 7")).ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Explain Should Not Invoke Scalar Or Aggregate Callbacks")]
    public async Task Explain_ShouldNotInvokeScalarOrAggregateCallbacksAsync()
    {
        int callbacks = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("explain");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_echo", value => { callbacks++; return value; }));
        builder.Functions.Add(GraphAggregateFunction.Create<long, long, long>("developer_sum",
            () => { callbacks++; return 0; }, (state, value) => { callbacks++; return state + value; },
            state => { callbacks++; return state; }, _ => 8));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "EXPLAIN RETURN developer_sum(developer_echo(1))")).ShouldNotBeEmpty();
        callbacks.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Custom Aggregates Should Honor Grouping Distinct Empty And Null Inputs")]
    public async Task Execute_CustomAggregates_ShouldHonorGroupingDistinctEmptyAndNullInputsAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("aggregates");
        builder.Functions.Add(GraphAggregateFunction.Create<long, long, long>("developer_sum", () => 0L, (state, value) => state + value, state => state, _ => 8));
        builder.Functions.Add(new ProductSum());
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "FOR v IN [1,2,2,NULL] RETURN developer_sum(v), developer_sum(DISTINCT v), developer_product(v,2), count(*), count(v)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 5L, 3L, 10L, 4L, 3L });
        (await Rows(session, "FOR v IN [] RETURN developer_sum(v), count(*), sum(v)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 0L, 0L, 0L });
        (await Rows(session, "FOR v IN [1,2,2] RETURN v, developer_sum(v) AS n ORDER BY n"))
            .ShouldBe(new[] { new object?[] { 1L, 1L }, new object?[] { 2L, 4L } }, ignoreOrder: false);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN developer_sum(developer_sum(1))"));
        error.Diagnostics[0].Code.ShouldBe("CG05001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Custom Aggregate Retained State Should Enforce Budget And Release")]
    public async Task Execute_CustomAggregateRetainedState_ShouldEnforceBudgetAndReleaseAsync()
    {
        int disposed = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("memory");
        builder.Options.QueryMemoryBudget = 65536;
        builder.Functions.Add(new RetainedFunction(() => disposed++));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "FOR v IN [1,2] RETURN developer_retained(v)"));
        error.Diagnostics[0].Code.ShouldBe("CG05020");
        disposed.ShouldBe(1);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        (await Rows(session, "RETURN 7")).ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Aggregate Reused Or Null Accumulator Should Be Refused")]
    public void Aggregate_ReusedOrNullAccumulator_ShouldBeRefused()
    {
        var function = new ReusedFunction();
        var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch);
        using var first = function.CreateAccumulator(in context);
        Should.Throw<DatabaseException>(() => CreateAgain(function)).Diagnostics[0].Code.ShouldBe("CG05021");
        static void CreateAgain(GraphAggregateFunction function)
        { var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch); function.CreateAccumulator(in context); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Declared Nested Shape Should Reject Scalar Route Before Writes")]
    public async Task Execute_DeclaredNestedShape_ShouldRejectScalarRouteBeforeWritesAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("shape");
        builder.Functions.Add(new ShapeFunction(GraphFunctionType.List(GraphFunctionType.Node.NotNull()), value => new object?[] { value }));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Original {n:1})", cancellationToken: CancellationToken.None);
        var request = new GraphQueryRequest(GraphQueryRequest.FromGql("MATCH (n:Original) INSERT (:Unwritten) RETURN developer_shape(n)").Statement) { ScalarsOnly = true };
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics[0].Code.ShouldBe("CG05001");
        (await Rows(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await Rows(session, "MATCH (n:Original) LET xs=developer_shape(n) FOR x IN xs RETURN x.n"))
            .ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Wrong Declared Return Should Rollback Writes And Code Failure")]
    public async Task Execute_WrongDeclaredReturn_ShouldRollbackWritesAndCodeFailureAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("bad-shape");
        builder.Functions.Add(new ShapeFunction(GraphFunctionType.Int64, _ => "wrong"));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "INSERT (:Unwritten) RETURN developer_shape(1)"));
        error.Diagnostics[0].Code.ShouldBe("CG05021");
        (await Rows(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Context Should Capture Database Token And Transaction Clock")]
    public async Task Execute_Context_ShouldCaptureDatabaseTokenAndTransactionClockAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("context");
        var observed = new List<(DatabaseName Name, DateTimeOffset Time, CancellationToken Token)>();
        builder.Functions.Add(new ContextFunction((name, time, token) => observed.Add((name, time, token))));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Hang guard, not a timing assertion.
        await session.ExecuteAsync("START TRANSACTION", cancellationToken: cancellation.Token);
        var first = await Rows(session, "RETURN developer_context(1), CURRENT_TIMESTAMP", cancellation.Token);
        first.ShouldHaveSingleItem()[0].ShouldBe(first[0][1]);
        await Rows(session, "RETURN developer_context(2)", cancellation.Token);
        await session.ExecuteAsync("ROLLBACK", cancellationToken: cancellation.Token);
        observed.Count.ShouldBe(2);
        observed.All(value => value.Name.ToString() == "graph").ShouldBeTrue();
        observed[0].Time.ShouldBe(observed[1].Time);
        observed.All(value => value.Token == cancellation.Token).ShouldBeTrue();
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Overloads Should Bind Arity Once And Reject Ambiguous Signatures")]
    public async Task Execute_Overloads_ShouldBindArityOnceAndRejectAmbiguousSignaturesAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("overloads");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_arity", value => value));
        builder.Functions.Add(GraphScalarFunction.Create<long, long, long>("developer_arity", (left, right) => left + right));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(GraphScalarFunction.Create<string, string>("developer_arity", value => value)));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(GraphAggregateFunction.Create<long, long, long>("developer_arity",
            () => 0, (state, value) => state + value, state => state, _ => 8)));
        await using var engine = await builder.BuildAsync();
        var overloads = engine.Functions.GetOverloads("DEVELOPER_ARITY");
        overloads.Count.ShouldBe(2);
        engine.Functions.GetOverloads("missing").ShouldBeEmpty();
        Should.Throw<NotSupportedException>(() => ((IList<GraphFunction>)overloads).Clear());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await Rows(session, "RETURN developer_arity(3), developer_arity(3,4)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 3L, 7L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Aggregate Finish And Attachment Should Be One Shot")]
    public void Aggregate_FinishAndAttachment_ShouldBeOneShot()
    {
        var function = GraphAggregateFunction.Create<long, long, long>("developer_sum",
            () => 0, (state, value) => state + value, state => state, _ => 8);
        var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch);
        using var state = function.CreateAccumulator(in context);
        state.Finish().ShouldBe(0L);
        Should.Throw<InvalidOperationException>(() => state.Finish());
        Should.Throw<InvalidOperationException>(() => AddAfterFinish(state));
        static void AddAfterFinish(GraphAggregateAccumulator state)
        { var args = new GraphArguments(new object?[] { 1L }); state.Add(in args); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Aggregate Concurrent Reuse Should Claim Exactly One Owner")]
    public async Task Aggregate_ConcurrentReuse_ShouldClaimExactlyOneOwnerAsync()
    {
        var function = new ReusedFunction();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await ready.Task; return Claim(function); });
        var second = Task.Run(async () => { await ready.Task; return Claim(function); });
        ready.SetResult();
        // This is a hang guard for a rendezvous, not a timing assertion.
        bool[] claims = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        claims.Count(value => value).ShouldBe(1);
        static bool Claim(GraphAggregateFunction function)
        {
            var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch);
            try { using var value = function.CreateAccumulator(in context); return true; }
            catch (DatabaseException error) when (error.Diagnostics.Count == 1 && error.Diagnostics[0].Code == "CG05021") { return false; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: Execute Application Infrastructure Failure Should Retain Callback Code And Cause")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Execute_ApplicationInfrastructureFailure_ShouldRetainCallbackCodeAndCauseAsync(int kind)
    {
        Exception failure = kind switch
        {
            0 => new System.IO.IOException("I/O sentinel"),
            1 => new TimeoutException("timeout sentinel"),
            _ => new Assimalign.Cohesion.Database.Storage.StorageIOException("storage sentinel"),
        };
        var builder = GraphDatabaseEngine.CreateBuilder("identity");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_infrastructure", _ => throw failure));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("START TRANSACTION", cancellationToken: CancellationToken.None);
        Exception? caught = null;
        try { await Rows(session, "RETURN developer_infrastructure(1)"); }
        catch (Exception error) { caught = error; }
        var boundary = caught.ShouldBeAssignableTo<DatabaseException>()!;
        boundary.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        boundary.InnerException.ShouldBeSameAs(failure);
        boundary.Message.ShouldContain(failure.Message, Case.Sensitive);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        var aborted = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN 1"));
        aborted.Diagnostics[0].Code.ShouldBe("CG04007");
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        (await Rows(session, "RETURN 7")).ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Aggregate Dispose Failure Should Release All Groups And Preserve Primary Failure")]
    public async Task Aggregate_DisposeFailure_ShouldReleaseAllGroupsAndPreservePrimaryFailureAsync()
    {
        int disposed = 0;
        var original = new InvalidOperationException("step failure");
        var builder = GraphDatabaseEngine.CreateBuilder("dispose");
        builder.Functions.Add(new ThrowingDisposeFunction(() => disposed++, original));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "FOR v IN [1,2] RETURN v, developer_dispose(v)"));
        failure.InnerException.ShouldBeSameAs(original);
        disposed.ShouldBe(2);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
    }

    private sealed class ThrowingDisposeFunction : GraphAggregateFunction
    {
        private readonly Action _disposed;
        private readonly Exception _failure;
        internal ThrowingDisposeFunction(Action disposed, Exception failure)
            : base("developer_dispose", [GraphFunctionType.Int64], GraphFunctionType.Int64)
        { _disposed = disposed; _failure = failure; }
        protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context) => new ThrowingDisposeState(_disposed, _failure);
    }
    private sealed class ThrowingDisposeState : GraphAggregateAccumulator
    {
        private readonly Action _disposed;
        private readonly Exception _failure;
        internal ThrowingDisposeState(Action disposed, Exception failure) { _disposed = disposed; _failure = failure; }
        protected override void AddCore(scoped in GraphArguments arguments)
        { if (arguments.Get<long>(0) == 2) { throw _failure; } }
        protected override object? FinishCore() => 0L;
        protected override long RetainedBytesCore => 8;
        protected override void DisposeCore() { _disposed(); throw new InvalidOperationException("dispose sentinel"); }
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Unknown Deferred Name Should Retain Code And Session")]
    public async Task Execute_UnknownDeferredName_ShouldRetainCodeAndSessionAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("unknown", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("RETURN developer_missing(1)");
        request.Statement.Diagnostics.ShouldBeEmpty();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00010");
        error.Diagnostics[0].Start.ShouldNotBeNull();
        (await Rows(session, "RETURN 1")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Catalog Native Functions Should Work Through Public Invocation Without Planner")]
    public async Task Catalog_NativeFunctions_ShouldWorkThroughPublicInvocationWithoutPlannerAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("native-public", new());
        NativeAggregate(engine.Functions, "count", new object?[][] { [null], [7L] }).ShouldBe(1L);
        NativeAggregate(engine.Functions, "count", new object?[][] { [], [] }).ShouldBe(2L);
        NativeAggregate(engine.Functions, "count", []).ShouldBe(0L);
        NativeAggregate(engine.Functions, "sum", new object?[][] { [null], [2L], [3L] }).ShouldBe(5L);
        NativeAggregate(engine.Functions, "sum", []).ShouldBe(0L);
        NativeAggregate(engine.Functions, "min", new object?[][] { [null], [2L], [1L] }).ShouldBe(1L);
        NativeAggregate(engine.Functions, "min", []).ShouldBeNull();
        var upper = engine.Functions.GetOverloads("upper").ShouldHaveSingleItem().ShouldBeAssignableTo<GraphScalarFunction>();
        var arguments = new GraphArguments(new object?[] { "abc" });
        upper.Invoke(in arguments).ShouldBe("ABC");
        static object? NativeAggregate(GraphFunctionCatalog catalog, string name, object?[][] rows)
        {
            var function = (GraphAggregateFunction)catalog.GetOverloads(name).Single();
            var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch);
            using var state = function.CreateAccumulator(in context);
            foreach (var row in rows) { var args = new GraphArguments(row, context); state.Add(in args); }
            return state.Finish();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: Aggregate Initial Retained Report Failure Should Dispose And Code")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregate_InitialRetainedReportFailure_ShouldDisposeAndCodeAsync(bool negative)
    {
        int disposed = 0;
        var original = new InvalidOperationException("retained-report sentinel");
        var builder = GraphDatabaseEngine.CreateBuilder("initial-report");
        builder.Functions.Add(new ReportFailureFunction(() => disposed++, negative, original));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN developer_report(1)"));
        error.Diagnostics[0].Code.ShouldBe("CG05021");
        if (!negative) { error.InnerException.ShouldBeSameAs(original); }
        disposed.ShouldBe(1);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
    }

    private sealed class ReportFailureFunction : GraphAggregateFunction
    {
        private readonly Action _disposed;
        private readonly bool _negative;
        private readonly Exception _failure;
        internal ReportFailureFunction(Action disposed, bool negative, Exception failure)
            : base("developer_report", [GraphFunctionType.Int64], GraphFunctionType.Int64)
        { _disposed = disposed; _negative = negative; _failure = failure; }
        protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context)
            => new ReportFailureState(_disposed, _negative, _failure);
    }
    private sealed class ReportFailureState : GraphAggregateAccumulator
    {
        private readonly Action _disposed;
        private readonly bool _negative;
        private readonly Exception _failure;
        internal ReportFailureState(Action disposed, bool negative, Exception failure) { _disposed = disposed; _negative = negative; _failure = failure; }
        protected override void AddCore(scoped in GraphArguments arguments) { }
        protected override object? FinishCore() => 0L;
        protected override long RetainedBytesCore => _negative ? -1 : throw _failure;
        protected override void DisposeCore() => _disposed();
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Named Native Aggregate Ast Should Use The Registered Pipeline")]
    public async Task Execute_NamedNativeAggregateAst_ShouldUseTheRegisteredPipelineAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("named-native", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var query = new GqlQueryExpression(Array.Empty<GqlMatchClause>(), [], [], false,
            [new GqlProjection(new GqlNamedFunctionCallExpression("count", []), "n"),
             new GqlProjection(new GqlNamedFunctionCallExpression("sum", [new GqlLiteralExpression(42L)]), "s")]);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBe(1L); row.GetValue(1).ShouldBe(42L); }
        engine.Functions.GetOverloads("relationships").ShouldBeSameAs(engine.Functions.GetOverloads("edges"));
        engine.Functions.GetOverloads("collect").ShouldBeSameAs(engine.Functions.GetOverloads("collect_list"));
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Arguments Get Should Handle Nullable Object And Wrong Types Explicitly")]
    public void Arguments_Get_ShouldHandleNullableObjectAndWrongTypesExplicitly()
    {
        var arguments = new GraphArguments(new object?[] { null, 42L, "text" });
        arguments.Get<long?>(0).ShouldBeNull();
        arguments.Get<string?>(0).ShouldBeNull();
        arguments.Get<object>(1).ShouldBe(42L);
        arguments.Get<string>(2).ShouldBe("text");
        Should.Throw<InvalidCastException>(ReadNullNonNullable);
        Should.Throw<InvalidCastException>(ReadWrongType);
        static void ReadNullNonNullable() { var args = new GraphArguments(new object?[] { null }); _ = args.Get<long>(0); }
        static void ReadWrongType() { var args = new GraphArguments(new object?[] { 42L }); _ = args.Get<int>(0); }
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Strict Non Null Callback Metadata Should Permit Null Short Circuit Without Losing Children")]
    public async Task Execute_StrictNonNullCallbackMetadata_ShouldPermitNullShortCircuitWithoutLosingChildrenAsync()
    {
        var builder = GraphDatabaseEngine.CreateBuilder("nullable");
        var node = new NodeIdentityFunction();
        var scalar = new ShapeFunction(GraphFunctionType.Int64.NotNull(), value => value);
        builder.Functions.Add(node); builder.Functions.Add(scalar);
        node.ReturnType.IsNullable.ShouldBeTrue();
        scalar.ReturnType.IsNullable.ShouldBeTrue();
        var list = GraphFunctionType.List(GraphFunctionType.Node.NotNull()).NotNull();
        list.Element!.IsNullable.ShouldBeFalse();
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("LET v=developer_shape(NULL) RETURN v", cancellationToken: CancellationToken.None))
        {
            result.Columns.ShouldHaveSingleItem().IsNullable.ShouldBeTrue();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBeNull(); }
        }
        var request = GraphPathsQueryRequest.FromGql("OPTIONAL MATCH (n:Missing) LET x=developer_node_identity(n) RETURN x");
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics[0].Code.ShouldBe("CG05001");
        error.Message.ShouldBe(ErrorMessages.OptionalPathProjection);
    }

    private sealed class NodeIdentityFunction : GraphScalarFunction
    {
        internal NodeIdentityFunction() : base("developer_node_identity", [GraphFunctionType.Node], GraphFunctionType.Node.NotNull()) { }
        protected override object? InvokeCore(scoped in GraphArguments arguments) => arguments[0];
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Named Native Shapes Should Match Enum Ast For Maps And Mandatory Nodes")]
    public async Task Execute_NamedNativeShapes_ShouldMatchEnumAstForMapsAndMandatoryNodesAsync()
    {
        await using var engine = GraphDatabaseEngine.Create("native-shapes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var map = new GqlMapExpression([new KeyValuePair<string, GqlExpression>("x", new GqlLiteralExpression(1L))]);
        var expression = new GqlNamedFunctionCallExpression("properties", [map]);
        var query = new GqlQueryExpression(Array.Empty<GqlMatchClause>(), [], [], false, [new GqlProjection(expression, "m")]);
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(query)) { ScalarsOnly = true }, CancellationToken.None))
        {
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { row.GetValue(0).ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["x"].ShouldBe(1L); }
        }
        await session.ExecuteAsync("INSERT (:A)-[:T]->(:B)", cancellationToken: CancellationToken.None);
        var parsed = GraphPathsQueryRequest.FromGql("MATCH p=()-[:T]->() FOR n IN nodes(p) RETURN n");
        var stages = parsed.Statement.GqlExpression.Stages.ToArray();
        var loop = stages.OfType<GqlForStage>().ShouldHaveSingleItem();
        int index = Array.IndexOf(stages, loop);
        stages[index] = new GqlForStage(loop.Variable, new GqlNamedFunctionCallExpression("nodes", [new GqlVariableExpression("p")]));
        var request = new GraphPathsQueryRequest(new GqlQueryStatement(new GqlQueryExpression(stages)));
        var paths = (GraphPathsQueryResult)await session.ExecuteAsync(request, CancellationToken.None);
        paths.Paths.Count.ShouldBe(2);
    }


    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Execute Empty Input Nested Function Entity Should Reject Cast Before Writes")]
    public async Task Execute_EmptyInputNestedFunctionEntity_ShouldRejectCastBeforeWritesAsync()
    {
        int callbacks = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("nested-empty-shape");
        builder.Functions.Add(new ShapeFunction(GraphFunctionType.List(GraphFunctionType.Node.NotNull()),
            _ => { callbacks++; return Array.Empty<object?>(); }));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => Rows(session,
            "MATCH (n:Missing) INSERT (:Unwritten) RETURN CAST(developer_shape(n)[0] AS STRING)"));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        callbacks.ShouldBe(0);
        (await Rows(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }


    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: Invoke Application Infrastructure Failure Should Retain Callback Code And Cause")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Invoke_ApplicationInfrastructureFailure_ShouldRetainCallbackCodeAndCause(int kind)
    {
        Exception failure = kind switch
        {
            0 => new System.IO.IOException("I/O sentinel"),
            1 => new TimeoutException("timeout sentinel"),
            _ => new Assimalign.Cohesion.Database.Storage.StorageIOException("storage sentinel"),
        };
        var function = GraphScalarFunction.Create<long, long>("developer_infrastructure", _ => throw failure);
        var arguments = new GraphArguments(new object?[] { 1L });
        Exception? caught = null;
        try { function.Invoke(in arguments); } catch (Exception error) { caught = error; }
        var coded = caught.ShouldBeAssignableTo<DatabaseException>()!;
        coded.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        coded.InnerException.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Invoke Canceled Callback Should Preserve Exact Identity")]
    public void Invoke_CanceledCallback_ShouldPreserveExactIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var function = GraphScalarFunction.Create<long, long>("developer_cancel", _ => { cancellation.Cancel(); throw failure; });
        var context = new GraphFunctionContext(default, DateTimeOffset.UnixEpoch, cancellation.Token);
        var arguments = new GraphArguments(new object?[] { 1L }, context);
        Exception? caught = null;
        try { function.Invoke(in arguments); } catch (Exception error) { caught = error; }
        caught.ShouldBeSameAs(failure);
    }

    private static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string source, CancellationToken token = default)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows;
    }

    private sealed class ShapeFunction : GraphScalarFunction
    {
        private readonly Func<object?, object?> _body;
        internal ShapeFunction(GraphFunctionType result, Func<object?, object?> body)
            : base("developer_shape", [GraphFunctionType.Any], result) { _body = body; }
        protected override object? InvokeCore(scoped in GraphArguments arguments) => _body(arguments[0]);
    }
    private sealed class ContextFunction : GraphScalarFunction
    {
        private readonly Action<DatabaseName, DateTimeOffset, CancellationToken> _observe;
        internal ContextFunction(Action<DatabaseName, DateTimeOffset, CancellationToken> observe)
            : base("developer_context", [GraphFunctionType.Int64], GraphFunctionType.Of<DateTimeOffset>()) { _observe = observe; }
        protected override object? InvokeCore(scoped in GraphArguments arguments)
        {
            var context = arguments.Context;
            _observe(context.DatabaseName, context.TransactionTimestamp, context.CancellationToken);
            return context.TransactionTimestamp;
        }
    }
    private sealed class ProductSum : GraphAggregateFunction
    {
        internal ProductSum() : base("developer_product", [GraphFunctionType.Int64, GraphFunctionType.Int64], GraphFunctionType.Int64) { }
        protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context) => new SumState();
    }
    private class SumState : GraphAggregateAccumulator
    {
        protected long Total;
        protected override void AddCore(scoped in GraphArguments arguments)
            => Total += arguments.Count == 2 ? arguments.Get<long>(0) * arguments.Get<long>(1) : arguments.Get<long>(0);
        protected override object? FinishCore() => Total;
        protected override long RetainedBytesCore => 8;
    }
    private sealed class RetainedFunction : GraphAggregateFunction
    {
        private readonly Action _disposed;
        internal RetainedFunction(Action disposed) : base("developer_retained", [GraphFunctionType.Int64], GraphFunctionType.Int64) { _disposed = disposed; }
        protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context) => new RetainedState(_disposed);
    }
    private sealed class RetainedState : SumState
    {
        private readonly Action _disposed;
        internal RetainedState(Action disposed) { _disposed = disposed; }
        protected override long RetainedBytesCore => Total == 0 ? 8 : 65536;
        protected override void DisposeCore() => _disposed();
    }
    private sealed class ReusedFunction : GraphAggregateFunction
    {
        private readonly SumState _state = new();
        internal ReusedFunction() : base("developer_reused", [GraphFunctionType.Int64], GraphFunctionType.Int64) { }
        protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context) => _state;
    }
}
