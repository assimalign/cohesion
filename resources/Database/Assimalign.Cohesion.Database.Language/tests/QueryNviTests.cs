using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

public sealed class QueryNviTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Analyzer validation precedes core dispatch")]
    [InlineData(false)]
    [InlineData(true)]
    public void AnalyzeAsync_NullContext_ShouldRejectBeforeCancellationAndCore(bool canceled)
    {
        // Arrange
        int calls = 0;
        QueryAnalyzer analyzer = new DelegateQueryAnalyzer((_, _) => { calls++; return Task.CompletedTask; });

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => analyzer.AnalyzeAsync(null!, new CancellationToken(canceled)))
            .ParamName.ShouldBe("context");
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Pre-canceled analysis never enters its core")]
    public void AnalyzeAsync_CanceledToken_ShouldRejectBeforeCore()
    {
        // Arrange
        int calls = 0;
        var context = CaptureContext();
        var token = new CancellationToken(canceled: true);
        QueryAnalyzer analyzer = new DelegateQueryAnalyzer((_, _) => { calls++; return Task.CompletedTask; });

        // Act / Assert
        Should.Throw<OperationCanceledException>(() => analyzer.AnalyzeAsync(context, token)).CancellationToken.ShouldBe(token);
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Valid analysis returns its core task unchanged")]
    public void AnalyzeAsync_ValidContext_ShouldPreserveTaskAndArguments()
    {
        // Arrange
        var context = CaptureContext();
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource();
        int calls = 0;
        QueryAnalyzer analyzer = new DelegateQueryAnalyzer((actualContext, actualToken) =>
        {
            actualContext.ShouldBeSameAs(context);
            actualToken.ShouldBe(cancellation.Token);
            calls++;
            return completion.Task;
        });

        // Act
        var task = analyzer.AnalyzeAsync(context, cancellation.Token);

        // Assert
        task.ShouldBeSameAs(completion.Task);
        calls.ShouldBe(1);
        completion.SetResult();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parser options are required by the base constructor")]
    public void Constructor_NullOptions_ShouldRejectImmediately()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new AnalyzerTestParser(null!)).ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parse runs grammar before successful analysis")]
    public void Parse_SuccessfulAnalyzer_ShouldRetainCoreOrdering()
    {
        // Arrange
        var order = new List<string>();
        var options = new QueryParserOptions();
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => { order.Add("analyze"); return Task.CompletedTask; }));
        QueryParser parser = new LifecycleTestParser(options, order);

        // Act
        parser.Parse("source");

        // Assert
        order.ShouldBe(["parse:source", "analyze"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Failed analysis propagates and permits another parse")]
    public void Parse_FaultedAnalyzer_ShouldPropagateAndRetainConfiguration()
    {
        // Arrange
        var order = new List<string>();
        var failure = new InvalidOperationException();
        var options = new QueryParserOptions();
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => Task.FromException(failure)));
        QueryParser parser = new LifecycleTestParser(options, order);

        // Act
        Should.Throw<InvalidOperationException>(() => parser.Parse("first")).ShouldBeSameAs(failure);
        options.Analyzers.Clear();
        parser.Parse("second");

        // Assert
        order.ShouldBe(["parse:first", "parse:second"]);
    }

    private static QueryAnalyzerContext CaptureContext()
    {
        QueryAnalyzerContext? captured = null;
        var options = new QueryParserOptions();
        options.Analyzers.Add(new DelegateQueryAnalyzer((context, _) =>
        {
            captured = context;
            return Task.CompletedTask;
        }));
        new AnalyzerTestParser(options).Parse("");
        return captured.ShouldNotBeNull();
    }
}
