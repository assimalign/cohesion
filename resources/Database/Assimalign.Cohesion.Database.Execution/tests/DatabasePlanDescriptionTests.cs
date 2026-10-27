using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Execution.Tests;

public sealed class DatabasePlanDescriptionTests
{
    [Fact(DisplayName = "Cohesion Test [Execution] - Plan description: optional estimate and actual counts retain their typed values (#1522)")]
    public async Task Create_Counts_ShouldPreserveZeroAndNonzeroValues()
    {
        await using var result = DatabasePlanDescription.Create([new DatabasePlanNode
        { NodeId = 0, Operator = "IndexSeek", EstimatedRows = 17, ActualRows = 0 }]);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.FieldCount.ShouldBe(8);
            row.IsNull(6).ShouldBeFalse();
            row.IsNull(7).ShouldBeFalse();
            row.GetInt64(6).ShouldBe(17);
            row.GetInt64(7).ShouldBe(0);
            count++;
        }
        count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Execution] - Plan description: materialization snapshots input and preserves nullable typed fields")]
    public async Task Create_MutableInput_ShouldSnapshotNodesAndPreserveNullsAsync()
    {
        // Arrange
        var input = new List<DatabasePlanNode>
        {
            new() { NodeId = 0, Operator = "Select" },
            new() { NodeId = 1, ParentId = 0, Operator = "IndexSeek", Target = "items", Index = "by_id", Detail = "id = 1" },
        };
        // Act
        await using var result = DatabasePlanDescription.Create(input);
        input.Clear();
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        // Assert
        rows.Count.ShouldBe(2);
        rows[0].GetInt32(0).ShouldBe(0);
        rows[0].IsNull(1).ShouldBeTrue();
        rows[0].GetString(1).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => rows[0].GetInt64(6));
        rows[1].GetInt32(1).ShouldBe(0);
        rows[1].GetString(4).ShouldBe("by_id");
        rows[1].IsNull(6).ShouldBeTrue();
        rows[1].IsNull(7).ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Execution] - Plan description: canceled enumeration stops before returning a row")]
    public async Task GetRowsAsync_CanceledToken_ShouldRefuseEnumerationAsync()
    {
        // Arrange
        await using var result = DatabasePlanDescription.Create([new DatabasePlanNode { NodeId = 0, Operator = "Scan" }]);
        using var source = new CancellationTokenSource();
        source.Cancel();
        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in result.GetRowsAsync(source.Token)) { _ = row; }
        });
    }
}
