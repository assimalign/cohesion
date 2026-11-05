using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

[Collection("OQL parser concurrency")]
public sealed class OqlParserConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Parser resets diagnostics and serializes concurrent calls")]
    public async Task Parse_ConcurrentCalls_ShouldResetDiagnosticsAndRetainEachSource()
    {
        // Arrange
        var parser = new OqlQueryParser();
        parser.Parse("SELECT FROM").Diagnostics.ShouldNotBeEmpty();
        parser.Parse("SELECT number FROM items").Diagnostics.ShouldBeEmpty();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        var workers = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var elapsed = Stopwatch.StartNew();
            int calls = 0;
            do
            {
                string source = $"SELECT {index} AS number FROM items";
                var statement = (OqlQueryStatement)parser.Parse(source);
                statement.Diagnostics.ShouldBeEmpty();
                var expression = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
                expression.Text.ShouldBe(source);
                expression.Projections.ShouldHaveSingleItem().Expression.ShouldBeOfType<OqlLiteralExpression>()
                    .Value.ShouldBeOfType<JsonElement>().GetInt32().ShouldBe(index);
                calls++;
                Interlocked.Increment(ref completed);
            }
            while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || calls < 12)
                && elapsed.Elapsed < TimeSpan.FromSeconds(30));
            calls.ShouldBeGreaterThanOrEqualTo(12);
        })).ToArray();

        // Act
        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        // Assert
        completed.ShouldBeGreaterThanOrEqualTo(144);
    }
}
