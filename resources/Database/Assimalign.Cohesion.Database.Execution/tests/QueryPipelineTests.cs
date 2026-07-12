using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution.Tests;

/// <summary>
/// Tests for the shared execution pipeline (#163/#164/#165): stage ordering and
/// short-circuiting, transaction boundary semantics for implicit and explicit
/// scopes, error propagation, and cancellation.
/// </summary>
public class QueryPipelineTests
{
    private sealed class TestStatement : QueryStatement
    {
        public TestStatement() => Expression = new TestExpression();

        public override QueryExpression Expression { get; }

        private sealed class TestExpression : QueryExpression
        {
        }
    }

    private sealed class TestRequest : QueryRequest
    {
        public TestRequest()
            : base(new TestStatement())
        {
        }
    }

    private sealed class TestTransactionScope : IQueryTransactionScope
    {
        public TestTransactionScope(bool isImplicit) => IsImplicit = isImplicit;

        public bool IsImplicit { get; }

        public QueryTransactionStatus Status { get; private set; } = QueryTransactionStatus.Active;

        public bool FailOnRollback { get; set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            Status = QueryTransactionStatus.Committed;
            return default;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (FailOnRollback)
            {
                Status = QueryTransactionStatus.Faulted;
                throw new InvalidOperationException("Simulated rollback failure.");
            }

            Status = QueryTransactionStatus.RolledBack;
            return default;
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class RecordingStage : IQueryPipelineStage
    {
        private readonly List<string> _order;
        private readonly string _name;

        public RecordingStage(List<string> order, string name)
        {
            _order = order;
            _name = name;
        }

        public async ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, QueryPipelineDelegate next, CancellationToken cancellationToken = default)
        {
            _order.Add($"{_name}:before");
            var result = await next(context, cancellationToken);
            _order.Add($"{_name}:after");
            return result;
        }
    }

    private static QueryExecutionContext Context(bool isImplicit, CancellationToken requestAborted = default)
        => new(new TestRequest(), new TestTransactionScope(isImplicit), requestAborted);

    private static QueryPipelineDelegate Terminal(QueryResultStatus status = QueryResultStatus.Success)
        => (_, _) => new ValueTask<QueryResult>(new QueryStatementResult(status, 1));

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Pipeline: stages run in order around the terminal")]
    public async Task ExecuteAsync_MultipleStages_ShouldRunInRegistrationOrder()
    {
        // Arrange
        var order = new List<string>();
        var pipeline = new QueryPipelineBuilder()
            .Use(new RecordingStage(order, "outer"))
            .Use(new RecordingStage(order, "inner"))
            .Build((context, token) =>
            {
                order.Add("terminal");
                return Terminal()(context, token);
            });

        // Act
        var result = await pipeline.ExecuteAsync(Context(isImplicit: false));

        // Assert
        result.Status.ShouldBe(QueryResultStatus.Success);
        order.ShouldBe(new[] { "outer:before", "inner:before", "terminal", "inner:after", "outer:after" });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Pipeline: a stage can short-circuit downstream execution")]
    public async Task ExecuteAsync_ShortCircuitingStage_ShouldSkipDownstream()
    {
        // Arrange
        bool terminalRan = false;
        var pipeline = new QueryPipelineBuilder()
            .Use(new ShortCircuitStage())
            .Build((context, token) =>
            {
                terminalRan = true;
                return Terminal()(context, token);
            });

        // Act
        var result = await pipeline.ExecuteAsync(Context(isImplicit: false));

        // Assert
        terminalRan.ShouldBeFalse();
        result.AffectedCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Boundaries: implicit scope commits on success")]
    public async Task ExecuteAsync_ImplicitScopeSuccess_ShouldCommit()
    {
        // Arrange
        var context = Context(isImplicit: true);
        var pipeline = new QueryPipelineBuilder().Build(Terminal());

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Boundaries: implicit scope rolls back on a failed result")]
    public async Task ExecuteAsync_ImplicitScopeErrorResult_ShouldRollback()
    {
        // Arrange
        var context = Context(isImplicit: true);
        var pipeline = new QueryPipelineBuilder().Build(Terminal(QueryResultStatus.Error));

        // Act
        var result = await pipeline.ExecuteAsync(context);

        // Assert: the result is returned (not thrown) but the scope rolled back.
        result.Status.ShouldBe(QueryResultStatus.Error);
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Boundaries: implicit scope rolls back and the exception propagates")]
    public async Task ExecuteAsync_TerminalThrows_ShouldRollbackAndPropagate()
    {
        // Arrange
        var context = Context(isImplicit: true);
        var pipeline = new QueryPipelineBuilder().Build(
            (_, _) => throw new InvalidOperationException("execution failed"));

        // Act / Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await pipeline.ExecuteAsync(context));
        exception.Message.ShouldBe("execution failed");
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Boundaries: rollback failure never masks the execution error")]
    public async Task ExecuteAsync_RollbackFails_ShouldStillPropagateOriginalError()
    {
        // Arrange
        var scope = new TestTransactionScope(isImplicit: true) { FailOnRollback = true };
        var context = new QueryExecutionContext(new TestRequest(), scope);
        var pipeline = new QueryPipelineBuilder().Build(
            (_, _) => throw new InvalidOperationException("root cause"));

        // Act / Assert: the original error surfaces; the scope is Faulted.
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await pipeline.ExecuteAsync(context));
        exception.Message.ShouldBe("root cause");
        scope.Status.ShouldBe(QueryTransactionStatus.Faulted);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Boundaries: explicit scope is never completed by the pipeline")]
    public async Task ExecuteAsync_ExplicitScope_ShouldStayActive()
    {
        // Arrange
        var context = Context(isImplicit: false);
        var pipeline = new QueryPipelineBuilder().Build(Terminal());

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert: the session owns the boundary.
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.Active);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Cancellation: a pre-cancelled request rolls back and throws")]
    public async Task ExecuteAsync_PreCancelledRequest_ShouldRollbackAndThrow()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = Context(isImplicit: true, requestAborted: cancellation.Token);
        bool terminalRan = false;
        var pipeline = new QueryPipelineBuilder().Build((c, t) =>
        {
            terminalRan = true;
            return Terminal()(c, t);
        });

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync(context));
        terminalRan.ShouldBeFalse();
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Cancellation: mid-execution cancellation propagates and rolls back")]
    public async Task ExecuteAsync_CancelledMidExecution_ShouldRollback()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var context = Context(isImplicit: true, requestAborted: cancellation.Token);
        var pipeline = new QueryPipelineBuilder().Build(async (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return await Terminal()(context, token);
        });

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync(context));
        context.Transaction.Status.ShouldBe(QueryTransactionStatus.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - Context: diagnostics accumulate across stages")]
    public async Task ExecuteAsync_StagesAddDiagnostics_ShouldAccumulate()
    {
        // Arrange
        var context = Context(isImplicit: false);
        var pipeline = new QueryPipelineBuilder()
            .Use(new DiagnosticStage("EX001"))
            .Use(new DiagnosticStage("EX002"))
            .Build(Terminal());

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert
        context.Diagnostics.Count.ShouldBe(2);
        context.Diagnostics[0].Code.ShouldBe("EX001");
        context.Diagnostics[1].Code.ShouldBe("EX002");
    }

    private sealed class ShortCircuitStage : IQueryPipelineStage
    {
        public ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, QueryPipelineDelegate next, CancellationToken cancellationToken = default)
            => new(new QueryStatementResult(QueryResultStatus.Success, 0));
    }

    private sealed class DiagnosticStage : IQueryPipelineStage
    {
        private readonly string _code;

        public DiagnosticStage(string code) => _code = code;

        public ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, QueryPipelineDelegate next, CancellationToken cancellationToken = default)
        {
            context.AddDiagnostic(new Diagnostic(_code, "stage diagnostic", 0, 0, DiagnosticSeverity.Information));
            return next(context, cancellationToken);
        }
    }
}
